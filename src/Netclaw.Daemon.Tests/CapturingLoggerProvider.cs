// -----------------------------------------------------------------------
// <copyright file="CapturingLoggerProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Tests;

/// <summary>
/// Records each log entry, for every category. <see cref="Entries"/> keeps the level and the
/// formatted text. <see cref="Records"/> also keeps the category, the structured state, the
/// active scope values, and the exception, which is what a sink such as OTLP receives.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;

    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ConcurrentQueue<CapturedLogRecord> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            owner.Entries.Enqueue((logLevel, message));

            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToArray()
                : [];
            var scopes = new List<object?>();
            owner._scopes?.ForEachScope(static (scope, list) => list.Add(scope), scopes);
            owner.Records.Enqueue(new CapturedLogRecord(category, logLevel, message, values, scopes, exception));
        }
    }
}

internal sealed record CapturedLogRecord(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Values,
    IReadOnlyList<object?> Scopes,
    Exception? Exception);
