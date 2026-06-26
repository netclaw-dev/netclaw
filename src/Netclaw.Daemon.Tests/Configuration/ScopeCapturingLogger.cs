// -----------------------------------------------------------------------
// <copyright file="ScopeCapturingLogger.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Test logger that records the state objects passed to <see cref="ILogger.BeginScope"/>
/// so tests can assert which scopes were opened around a logging call. Returns
/// <c>null</c> from <c>BeginScope</c> (a valid no-op for <c>using var</c>); only the
/// captured state matters.
/// </summary>
internal sealed class ScopeCapturingLogger : ILogger
{
    private const string SessionIdKey = "SessionId";

    public List<object?> Scopes { get; } = [];

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        Scopes.Add(state);
        return null;
    }

    /// <summary>True if a scope tagging the given session id was opened.</summary>
    public bool HasSessionScope(string expectedId) =>
        Scopes.Any(s => s is IEnumerable<KeyValuePair<string, object>> kvps
            && kvps.Any(kv => kv.Key == SessionIdKey && kv.Value is string v && v == expectedId));

    /// <summary>True if any session-id scope (regardless of value) was opened.</summary>
    public bool HasAnySessionScope() =>
        Scopes.Any(s => s is IEnumerable<KeyValuePair<string, object>> kvps
            && kvps.Any(kv => kv.Key == SessionIdKey));
}
