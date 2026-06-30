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
    private const int DaemonLogFlushBatch = 256; // flush cap under a burst; idle flushes every line

    private readonly string _basePath;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly BlockingCollection<string> _queue = new(1024);
    private readonly Thread _writerThread;
    private IExternalScopeProvider? _scopeProvider;

    // The dispatcher reference is the only shared state on the routing path: null until
    // IRequiredActor.GetAsync resolves it, then written once (Volatile). No buffer/lock — a line
    // that finds it still null (the brief startup window, or a resolution failure) falls back to
    // daemon.log, which is the documented routing-off behavior.
    private IActorRef? _sessionDispatcher;
    private int _attached;
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
    /// Resolves the per-session log dispatcher in the background (the caller passes
    /// <c>IRequiredActor&lt;SessionLogDispatcherActorKey&gt;.GetAsync()</c>) and publishes it.
    /// Resolving in the background — rather than blocking — keeps this safe to call from a hosted
    /// service that starts before Akka. Session-tagged lines logged before it resolves (a brief
    /// startup window in which session actors do not yet exist) fall back to <c>daemon.log</c>, as
    /// do all session-tagged lines if resolution fails (a single ERR beacon records that).
    /// </summary>
    public void AttachSessionDispatcher(Task<IActorRef> dispatcherTask)
    {
        if (Interlocked.Exchange(ref _attached, 1) == 1)
            return;

        _ = ResolveSessionDispatcherAsync(dispatcherTask);
    }

    /// <summary>
    /// Partitions one already-formatted line: routes it to its session's <c>session.log</c> when
    /// it carries a session id (and is not the session-log writer's own line) and the dispatcher
    /// is resolved; otherwise writes it to <c>daemon.log</c>. <paramref name="stateSessionId"/> is
    /// the id found on the log event's state; the active scopes are consulted only as a fallback.
    /// </summary>
    internal void Route(string line, string? stateSessionId, string? stateSubSessionId, bool fromSessionLogActor)
    {
        // Carve-out: the session-log writer's own lines go to daemon.log so a failed write that
        // logs an error cannot route back into the same (failing) session.log — an infinite loop.
        if (fromSessionLogActor)
        {
            _queue.TryAdd(line);
            return;
        }

        var sessionId = stateSessionId;
        var subSessionId = stateSubSessionId;
        if (sessionId is null)
        {
            // Only chat-client lines lack a state session id; they carry both ids via BeginScope.
            // An actor line already names its ids in state, so we never consult scopes for it —
            // otherwise an unrelated ambient SubSessionId scope could hijack its routing.
            FindScopeIds(out var scopeSessionId, out var scopeSubSessionId);
            sessionId = scopeSessionId;
            subSessionId ??= scopeSubSessionId;
        }

        // Partition the LOCAL file: a sub-agent's lines (which carry a SubSessionId) get their own
        // session.log keyed by the sub-session; everything else goes to its session's file. The
        // line still carries the parent SessionId, so OTEL groups it under the parent regardless.
        var routingId = subSessionId ?? sessionId;
        if (!string.IsNullOrWhiteSpace(routingId) && Volatile.Read(ref _sessionDispatcher) is { } dispatcher)
        {
            dispatcher.Tell(new SessionLogDiagnostic(new SessionId(routingId), line));
            return;
        }

        // No session id, the dispatcher hasn't resolved yet (startup window), or resolution
        // failed → daemon.log.
        _queue.TryAdd(line);
    }

    private void FindScopeIds(out string? sessionId, out string? subSessionId)
    {
        sessionId = null;
        subSessionId = null;

        var scopeProvider = _scopeProvider;
        if (scopeProvider is null)
            return;

        // static lambda + a single mutable holder: the delegate is cached and nothing is
        // captured, so the chat-client diagnostic hot path doesn't allocate a closure per line.
        var ids = new ScopeIds();
        scopeProvider.ForEachScope(
            static (scope, state) =>
            {
                if (state.SessionId is not null && state.SubSessionId is not null)
                    return;

                if (scope is IEnumerable<KeyValuePair<string, object>> kvps)
                {
                    foreach (var kv in kvps)
                    {
                        if (state.SessionId is null
                            && kv.Key == NetclawLogProperties.SessionId
                            && kv.Value?.ToString() is { Length: > 0 } id)
                            state.SessionId = id;
                        else if (state.SubSessionId is null
                            && kv.Key == NetclawLogProperties.SubSessionId
                            && kv.Value?.ToString() is { Length: > 0 } subId)
                            state.SubSessionId = subId;
                    }
                }
            },
            ids);

        sessionId = ids.SessionId;
        subSessionId = ids.SubSessionId;
    }

    private sealed class ScopeIds
    {
        public string? SessionId;
        public string? SubSessionId;
    }

    private async Task ResolveSessionDispatcherAsync(Task<IActorRef> dispatcherTask)
    {
        try
        {
            // Publish once. After this, every Route fast-path reads it and Tells directly.
            Volatile.Write(ref _sessionDispatcher, await dispatcherTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // Resolution failed: _sessionDispatcher stays null, so session-tagged lines keep
            // falling back to daemon.log. One loud beacon, with a stderr fallback so the single
            // failure signal survives even a saturated daemon-log queue.
            var beacon = $"{GetTimestamp()} [ERR] Netclaw.Logging: session log dispatcher resolution failed; per-session routing disabled. {ex.Message}";
            if (!_queue.TryAdd(beacon))
                Console.Error.WriteLine(beacon);
        }
    }

    private void ProcessQueue()
    {
        var batched = 0;
        foreach (var message in _queue.GetConsumingEnumerable())
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(message);

                // Flush when the queue has drained — so sparse daemon.log lines (startup, config,
                // lifecycle, alerts) stay immediately durable — or after a burst batch, so a
                // sustained burst doesn't pay an fsync per line. The final tail is flushed by
                // Dispose() when the writer thread exits.
                if (_queue.Count == 0 || ++batched >= DaemonLogFlushBatch)
                {
                    _writer.Flush();
                    batched = 0;
                }
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

        // Single pass over the event's structured state: pick up the session id and (for a
        // sub-agent's lines) the sub-session id (for routing), the Akka log source (for a useful
        // per-line label — every actor shares the generic MEL category "Akka.Actor.ActorSystem"),
        // and whether this line is the session-log writer's own (so we never route it back into
        // the file it writes).
        ScanState(state, out var sessionId, out var subSessionId, out var logSource, out var fromSessionLogActor);

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

        _provider.Route(line, sessionId, subSessionId, fromSessionLogActor);
    }

    // Read the fields the producer already put on the log event. The Akka→MEL bridge passes an
    // AkkaLogState carrying WithContext("SessionId", …) plus "LogSource"/"ActorPath"; MEL's own
    // structured logging passes FormattedLogValues carrying a {SessionId} field. Both surface
    // their fields as KeyValuePair<string, object> sequences (the nullable-annotated and
    // unannotated forms are the same runtime type), so one branch reads both — no Akka internals.
    private static void ScanState<TState>(TState state, out string? sessionId, out string? subSessionId, out string? logSource, out bool fromSessionLogActor)
    {
        sessionId = null;
        subSessionId = null;
        logSource = null;
        fromSessionLogActor = false;

        if (state is IEnumerable<KeyValuePair<string, object>> fields)
        {
            foreach (var field in fields)
                Apply(field.Key, field.Value, ref sessionId, ref subSessionId, ref logSource, ref fromSessionLogActor);
        }
    }

    private static void Apply(string key, object? value, ref string? sessionId, ref string? subSessionId, ref string? logSource, ref bool fromSessionLogActor)
    {
        if (value is null)
            return;

        if (key == NetclawLogProperties.SessionId)
        {
            if (value.ToString() is { Length: > 0 } id)
                sessionId = id;
        }
        else if (key == NetclawLogProperties.SubSessionId)
        {
            if (value.ToString() is { Length: > 0 } subId)
                subSessionId = subId;
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
