// -----------------------------------------------------------------------
// <copyright file="ChatClientSessionScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Opens a logging scope tagging a chat-client decorator's log lines with the owning
/// <c>SessionId</c>, read from <see cref="SessionScopedChatOptions"/> on the call's
/// <see cref="ChatOptions"/>. The OTLP exporter has <c>IncludeScopes</c> enabled, so the
/// scope surfaces <c>SessionId</c> as a filterable attribute in Seq — matching the
/// <c>WithContext("SessionId", …)</c> key the session/channel actors already use, so
/// LLM-pipeline diagnostics (timing, retries, provider failover/outage) correlate to the
/// same session as the actor logs.
///
/// The decorators are session-agnostic singletons, so the id must arrive on the call
/// seam: this reads it from the options object rather than ambient context (which would
/// not flow across the actor mailbox boundary). Returns <c>null</c> — a no-op
/// <c>using</c> — for sidecar/session-agnostic calls that carry a plain
/// <see cref="ChatOptions"/>.
/// </summary>
internal static class ChatClientSessionScope
{
    public static IDisposable? Begin(ILogger logger, ChatOptions? options) =>
        options is SessionScopedChatOptions { SessionId: { Length: > 0 } sessionId }
            ? logger.BeginScope(new[] { new KeyValuePair<string, object>(NetclawLogProperties.SessionId, sessionId) })
            : null;
}
