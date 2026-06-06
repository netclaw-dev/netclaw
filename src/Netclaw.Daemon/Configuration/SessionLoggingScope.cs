// -----------------------------------------------------------------------
// <copyright file="SessionLoggingScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Shared helper for tagging chat-client log lines with the ambient session id.
///
/// The id lives in <see cref="SessionDiagnosticsContext"/> (an AsyncLocal pushed by
/// every session-owned path). The OTLP log exporter has <c>IncludeScopes</c> enabled
/// but cannot read that AsyncLocal directly, so without a logging scope LLM logs reach
/// Seq with no session correlation. Opening a scope keyed <c>SessionId</c> surfaces it
/// as a filterable attribute — matching the <c>WithContext("SessionId", …)</c> key the
/// session/channel actors already use, so a single attribute correlates actor and
/// chat-client logs.
///
/// MEL scopes are per-<see cref="ILogger"/>, so each chat-client decorator that emits
/// its own log lines (e.g. <see cref="LoggingChatClient"/> and the routing/failover
/// client) opens its own scope from this helper.
/// </summary>
internal static class SessionLoggingScope
{
    private const string SessionIdKey = "SessionId";

    /// <summary>
    /// Opens a scope tagging subsequent log lines on <paramref name="logger"/> with the
    /// ambient session id, or returns <c>null</c> when no session is in scope (a no-op
    /// <c>using</c>). A single-entry array avoids a per-call dictionary allocation while
    /// still presenting as <c>IEnumerable&lt;KeyValuePair&gt;</c>, which is what the OTLP
    /// exporter projects into log attributes.
    /// </summary>
    public static IDisposable? Begin(ILogger logger)
    {
        var sessionId = SessionDiagnosticsContext.SessionId;
        return sessionId is null
            ? null
            : logger.BeginScope(new[] { new KeyValuePair<string, object>(SessionIdKey, sessionId) });
    }
}
