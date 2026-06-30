// -----------------------------------------------------------------------
// <copyright file="RollingFileLoggerProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
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

    // The startup-window buffer and its dispatcher/failed transition are all guarded by
    // _routeGate. The lock is only ever taken on the slow path (before _sessionDispatcher is
    // published); once resolved, Route's fast path reads _sessionDispatcher lock-free.
    private readonly object _routeGate = new();
    private Queue<SessionLogDiagnostic>? _pendingDiagnostics;
    private IActorRef? _sessionDispatcher;
    private bool _routingFailed;
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

        lock (_routeGate)
            _pendingDiagnostics = new Queue<SessionLogDiagnostic>();

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

        // Fast path: once resolved, _sessionDispatcher is non-null for the rest of the process,
        // so the steady state never takes the lock.
        var dispatcher = Volatile.Read(ref _sessionDispatcher);
        if (dispatcher is not null)
        {
            dispatcher.Tell(diagnostic);
            return;
        }

        // Slow path — only during the brief window before resolution (or after a failure).
        // Serialize with ResolveSessionDispatcherAsync so the buffer is never enqueued-into after
        // it has been drained/abandoned (no lost lines, no null-deref on a torn-down buffer).
        lock (_routeGate)
        {
            if (_sessionDispatcher is { } resolved)
            {
                resolved.Tell(diagnostic);
                return;
            }

            // Routing failed, the buffer is gone, or it is full → fall back to daemon.log rather
            // than drop the line.
            if (_routingFailed || _pendingDiagnostics is null || _pendingDiagnostics.Count >= PreResolutionBufferLimit)
            {
                _queue.TryAdd(line);
                return;
            }

            _pendingDiagnostics.Enqueue(diagnostic);
        }
    }

    private string? FindSessionIdInScopes()
    {
        var scopeProvider = _scopeProvider;
        if (scopeProvider is null)
            return null;

        // static lambda + StrongBox state: the delegate is cached and nothing is captured, so the
        // chat-client diagnostic hot path doesn't allocate a closure per logged line.
        var box = new StrongBox<string?>(null);
        scopeProvider.ForEachScope(
            static (scope, state) =>
            {
                if (state.Value is not null)
                    return;

                if (scope is IEnumerable<KeyValuePair<string, object>> kvps)
                {
                    foreach (var kv in kvps)
                    {
                        if (kv.Key == NetclawLogProperties.SessionId
                            && kv.Value?.ToString() is { Length: > 0 } id)
                        {
                            state.Value = id;
                            return;
                        }
                    }
                }
            },
            box);

        return box.Value;
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
            // Resolution failed permanently. Mark routing failed and FLUSH the buffered lines to
            // daemon.log (rather than drop them) so the diagnostics that preceded the failure
            // aren't lost; session-scoped lines then fall back to daemon.log for the rest of the
            // process.
            lock (_routeGate)
            {
                _routingFailed = true;
                if (_pendingDiagnostics is not null)
                {
                    while (_pendingDiagnostics.TryDequeue(out var pending))
                        _queue.TryAdd(pending.Line);
                    _pendingDiagnostics = null;
                }
            }

            // One loud beacon, with a stderr fallback so the single failure signal is never lost
            // even if the daemon-log queue is saturated.
            var beacon = $"{GetTimestamp()} [ERR] Netclaw.Logging: session log dispatcher resolution failed; per-session routing disabled. {ex.Message}";
            if (!_queue.TryAdd(beacon))
                Console.Error.WriteLine(beacon);
            return;
        }

        // Publish the ref and drain the buffer under the same lock that Route's slow path uses,
        // so a line is never enqueued into the buffer after this drain has run.
        lock (_routeGate)
        {
            Volatile.Write(ref _sessionDispatcher, dispatcher);
            if (_pendingDiagnostics is not null)
            {
                while (_pendingDiagnostics.TryDequeue(out var pending))
                    dispatcher.Tell(pending);
                _pendingDiagnostics = null;
            }
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
