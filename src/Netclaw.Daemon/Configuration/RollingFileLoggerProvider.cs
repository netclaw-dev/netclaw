using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Simple file-based logger that writes to a daily rolling log file.
/// Uses a background queue to avoid blocking callers.
/// </summary>
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB per file
    private readonly string _basePath;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly BlockingCollection<string> _queue = new(1024);
    private readonly Thread _writerThread;
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

    internal void Enqueue(string message)
    {
        _queue.TryAdd(message);
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

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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
