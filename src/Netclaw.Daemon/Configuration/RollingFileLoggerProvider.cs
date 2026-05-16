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
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Simple file-based logger that writes to a daily rolling log file.
/// Uses a background queue to avoid blocking callers.
///
/// Session-scoped lines (emitted under a populated
/// <see cref="SessionDiagnosticsContext"/>) are mirrored to a per-session
/// <c>session.log</c> by routing through the <c>SessionLogDispatcher</c>
/// actor. The dispatcher serializes all writes for a given session through
/// a single mailbox, replacing the in-process file lock that previously
/// coordinated concurrent writers.
///
/// The dispatcher is wired in via <see cref="AttachSessionDispatcher"/>
/// post-construction (typically from an <c>IHostedService</c> that runs
/// after the actor system starts) because the provider is constructed
/// during host build, before Akka.
/// </summary>
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB per file
    private const int PreResolutionBufferLimit = 1000;

    private readonly string _basePath;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly BlockingCollection<string> _queue = new(1024);
    private readonly Thread _writerThread;
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

    /// <summary>
    /// Enables session-scoped log routing through the dispatcher actor.
    /// The provider buffers session-scoped diagnostic lines emitted between
    /// this call and dispatcher resolution into a small bounded queue, then
    /// drains them in order. Subsequent emits go straight to the dispatcher.
    /// Failures during resolution surface a single ERR line in the daemon log
    /// and disable session routing for the rest of the process.
    /// </summary>
    public void AttachSessionDispatcher(Task<IActorRef> dispatcherTask)
    {
        if (Interlocked.Exchange(ref _sessionRoutingEnabled, 1) == 1)
            return;

        _pendingDiagnostics = new ConcurrentQueue<SessionLogDiagnostic>();
        _ = ResolveSessionDispatcherAsync(dispatcherTask);
    }

    internal void Enqueue(string message)
    {
        _queue.TryAdd(message);

        if (_sessionRoutingEnabled == 0)
            return;

        var sessionId = SessionDiagnosticsContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var dispatcher = Volatile.Read(ref _sessionDispatcher);
        if (dispatcher is null && Volatile.Read(ref _pendingCount) >= PreResolutionBufferLimit)
            return;

        var diagnostic = new SessionLogDiagnostic(
            new SessionId(sessionId),
            $"[{_timeProvider.GetUtcNow():o}] Diagnostic: {message}");

        if (dispatcher is not null)
        {
            dispatcher.Tell(diagnostic);
            return;
        }

        Interlocked.Increment(ref _pendingCount);
        _pendingDiagnostics!.Enqueue(diagnostic);
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
            // Resolution failed permanently. Drop the buffer and switch the
            // session-log path off — daemon-global logging continues normally,
            // but session-scoped diagnostics will not appear in session.log
            // for the remainder of this process. Surface a single loud line
            // so operators see this in the daemon log instead of silently
            // accumulating drops.
            _queue.TryAdd($"{GetTimestamp()} [ERR] Netclaw.Logging: session log dispatcher resolution failed; session-scoped diagnostics disabled. {ex.Message}");
            _pendingDiagnostics = null;
            return;
        }

        // Publish the ref BEFORE draining so producers racing with the drainer
        // see the dispatcher and Tell directly rather than enqueueing into a
        // queue that we are about to abandon.
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
                // Last-resort: write to stderr to avoid silent swallow
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

        var timestamp = _provider.GetTimestamp();
        var level = logLevel switch
        {
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "DBG"
        };

        var message = formatter(state, exception);
        var line = $"{timestamp} [{level}] {_category}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        _provider.Enqueue(line);
    }
}
