// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Globalization;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// File-based logger that owns the LOCAL partition of the one log stream. Every line is
/// written to exactly one place on disk:
/// <list type="bullet">
/// <item>A line that carries a session id (an actor's <c>WithContext("SessionId", …)</c>,
/// which the Akka→MEL bridge surfaces as structured state; a MEL <c>{SessionId}</c> message
/// field; or a <c>BeginScope</c> carrying it) is routed to that session's <c>session.log</c>
/// via the <c>SessionLogDispatcher</c> and is NOT written to <c>daemon.log</c>.</item>
/// <item>Everything else — genuinely daemon-wide lines (startup, config, session lifecycle,
/// global errors) — goes to <c>daemon.log</c>.</item>
/// </list>
/// The full stream still reaches OTEL via the separate OTLP exporter (with the session id as
/// an attribute); the OTEL receiver does the global slicing/distilling. The dispatcher is
/// attached post-construction via <see cref="AttachSessionDispatcher"/> (the provider is built
/// during host build, before Akka). Lines emitted by the <see cref="SessionLogActor"/> itself
/// are forced to <c>daemon.log</c> so a write-failure log can never recurse into the file that
/// just failed.
/// </summary>
internal sealed class RollingFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB per file
    private const int PreResolutionBufferLimit = 1000;

    private readonly string _basePath;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly BlockingCollection<string> _queue = new(1024);
    private readonly Thread _writerThread;
    private IExternalScopeProvider? _scopeProvider;
    private ConcurrentQueue<SessionLogDiagnostic>? _pendingDiagnostics;
    private IActorRef? _sessionDispatcher;
    private int _pendingCount;
    private int _sessionRoutingEnabled;
    private StreamWriter? _writer;
    private string _currentDate = "";

    public RollingFileLoggerProvider(string basePath, TimeProvider? timeProvider = null)
    {
        _basePath = basePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _writerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "NetclawLogWriter"
        };
        _writerThread.Start();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    // MEL hands every scope-aware provider the same shared scope provider; the chat-client
    // decorators carry the session id via BeginScope, so we read it from here at emit time.
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    /// <summary>
    /// Enables per-session routing once the actor system is up. Session-tagged lines emitted
    /// between this call and dispatcher resolution are held in a small bounded buffer and then
    /// drained in order; afterwards they Tell the dispatcher directly. If resolution fails, a
    /// single ERR line is written to <c>daemon.log</c> and routing is disabled (session-tagged
    /// lines fall back to <c>daemon.log</c>) for the rest of the process.
    /// </summary>
    public void AttachSessionDispatcher(Task<IActorRef> dispatcherTask)
    {
        if (Interlocked.Exchange(ref _sessionRoutingEnabled, 1) == 1)
            return;

        _pendingDiagnostics = new ConcurrentQueue<SessionLogDiagnostic>();
        _ = ResolveSessionDispatcherAsync(dispatcherTask);
    }

    /// <summary>
    /// Partitions one already-formatted line: routes it to its session's <c>session.log</c>
    /// when it carries a session id (and is not from the session-log writer itself), otherwise
    /// writes it to <c>daemon.log</c>. <paramref name="stateSessionId"/> is the id found on the
    /// log event's state; the active scopes are consulted only as a fallback.
    /// </summary>
    internal void Route(string line, string? stateSessionId, bool fromSessionLogActor)
    {
        // Carve-out: the session-log writer's own lines go to daemon.log so a failed write that
        // logs an error cannot route back into the same (failing) session.log — an infinite loop.
        var sessionId = fromSessionLogActor ? null : (stateSessionId ?? FindSessionIdInScopes());

        if (string.IsNullOrWhiteSpace(sessionId) || _sessionRoutingEnabled == 0)
        {
            _queue.TryAdd(line);
            return;
        }

        var diagnostic = new SessionLogDiagnostic(new SessionId(sessionId), line);

        var dispatcher = Volatile.Read(ref _sessionDispatcher);
        if (dispatcher is not null)
        {
            dispatcher.Tell(diagnostic);
            return;
        }

        // Dispatcher not resolved yet. Buffer up to a bound; once full, fall back to daemon.log
        // rather than drop the line.
        if (Volatile.Read(ref _pendingCount) >= PreResolutionBufferLimit)
        {
            _queue.TryAdd(line);
            return;
        }

        Interlocked.Increment(ref _pendingCount);
        _pendingDiagnostics!.Enqueue(diagnostic);
    }

    private string? FindSessionIdInScopes()
    {
        var scopeProvider = _scopeProvider;
        if (scopeProvider is null)
            return null;

        string? found = null;
        scopeProvider.ForEachScope(
            (scope, _) =>
            {
                if (found is not null)
                    return;

                if (scope is IEnumerable<KeyValuePair<string, object>> kvps)
                {
                    foreach (var kv in kvps)
                    {
                        if (kv.Key == NetclawLogProperties.SessionId
                            && kv.Value?.ToString() is { Length: > 0 } id)
                        {
                            found = id;
                            return;
                        }
                    }
                }
            },
            (object?)null);

        return found;
    }

    private async Task ResolveSessionDispatcherAsync(Task<IActorRef> dispatcherTask)
    {
        IActorRef dispatcher;
        try
        {
            dispatcher = await dispatcherTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Resolution failed permanently. Drop the buffer and switch routing off — daemon
            // logging continues, but session-scoped lines fall back to daemon.log for the rest
            // of the process. Surface one loud line so this is visible, not silently degraded.
            _queue.TryAdd($"{GetTimestamp()} [ERR] Netclaw.Logging: session log dispatcher resolution failed; per-session routing disabled. {ex.Message}");
            Volatile.Write(ref _sessionRoutingEnabled, 0);
            _pendingDiagnostics = null;
            return;
        }

        // Publish the ref BEFORE draining so producers racing with the drainer Tell directly
        // rather than enqueueing into a buffer we are about to abandon.
        Volatile.Write(ref _sessionDispatcher, dispatcher);

        while (_pendingDiagnostics!.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref _pendingCount);
            dispatcher.Tell(pending);
        }
    }

    private void ProcessQueue()
    {
        foreach (var message in _queue.GetConsumingEnumerable())
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(message);
                _writer.Flush();
            }
            catch (Exception ex)
            {
                // Last-resort: write to stderr to avoid silent swallow.
                Console.Error.WriteLine($"[NetclawLogWriter] Failed to write log: {ex.Message}");
            }
        }
    }

    private void EnsureWriter()
    {
        var today = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_writer is not null && _currentDate == today)
        {
            // Roll if file exceeds size limit
            if (_writer.BaseStream.Length >= MaxFileSizeBytes)
            {
                _writer.Dispose();
                _writer = null;
            }
            else
            {
                return;
            }
        }

        _writer?.Dispose();
        _currentDate = today;

        var dir = Path.GetDirectoryName(_basePath)!;
        var name = Path.GetFileNameWithoutExtension(_basePath);
        var ext = Path.GetExtension(_basePath);
        var path = Path.Combine(dir, $"{name}-{today}{ext}");

        _writer = new StreamWriter(path, append: true) { AutoFlush = false };
    }

    internal string GetTimestamp()
    {
        return _timeProvider.GetUtcNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _writer?.Dispose();
    }
}

internal sealed class RollingFileLogger : ILogger
{
    private readonly string _category;
    private readonly RollingFileLoggerProvider _provider;

    public RollingFileLogger(string category, RollingFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        // Single pass over the event's structured state: pick up the session id (for routing),
        // the Akka log source (for a useful per-line label — every actor shares the generic MEL
        // category "Akka.Actor.ActorSystem"), and whether this line is the session-log writer's
        // own (so we never route it back into the file it writes).
        ScanState(state, out var sessionId, out var logSource, out var fromSessionLogActor);

        var timestamp = _provider.GetTimestamp();
        var level = logLevel switch
        {
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "DBG"
        };

        var source = logSource ?? _category;
        var message = formatter(state, exception);
        var line = $"{timestamp} [{level}] {source}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        _provider.Route(line, sessionId, fromSessionLogActor);
    }

    // Read the fields the producer already put on the log event. The Akka→MEL bridge passes an
    // AkkaLogState carrying WithContext("SessionId", …) plus "LogSource"/"ActorPath"; MEL's own
    // structured logging passes FormattedLogValues carrying a {SessionId} field. Both surface
    // their fields as KeyValuePair<string, object> sequences (the nullable-annotated and
    // unannotated forms are the same runtime type), so one branch reads both — no Akka internals.
    private static void ScanState<TState>(TState state, out string? sessionId, out string? logSource, out bool fromSessionLogActor)
    {
        sessionId = null;
        logSource = null;
        fromSessionLogActor = false;

        if (state is IEnumerable<KeyValuePair<string, object>> fields)
        {
            foreach (var field in fields)
                Apply(field.Key, field.Value, ref sessionId, ref logSource, ref fromSessionLogActor);
        }
    }

    private static void Apply(string key, object? value, ref string? sessionId, ref string? logSource, ref bool fromSessionLogActor)
    {
        if (value is null)
            return;

        if (key == NetclawLogProperties.SessionId)
        {
            if (value.ToString() is { Length: > 0 } id)
                sessionId = id;
        }
        else if (key == "LogSource")
        {
            logSource = value.ToString();
            if (IsSessionLogActorSource(logSource))
                fromSessionLogActor = true;
        }
        else if (key == "ActorPath")
        {
            if (IsSessionLogActorSource(value.ToString()))
                fromSessionLogActor = true;
        }
    }

    // The session-log writer (and its dispatcher) must never have its own lines routed back to
    // session.log. All actors share one MEL category, so identify it by Akka log source instead:
    // the actor type (SessionLogActor) or the dispatcher path ("session-log-dispatcher").
    private static bool IsSessionLogActorSource(string? source) =>
        source is not null
        && (source.Contains(nameof(SessionLogActor), StringComparison.Ordinal)
            || source.Contains("session-log-dispatcher", StringComparison.Ordinal));
}
