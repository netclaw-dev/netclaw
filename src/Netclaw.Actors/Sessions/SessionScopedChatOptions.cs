// -----------------------------------------------------------------------
// <copyright file="SessionScopedChatOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// <see cref="ChatOptions"/> that also names the owning session, so the
/// session-agnostic chat-client decorators (logging / retry / routing, which live in
/// <c>Netclaw.Daemon</c> and are shared singletons across every session) can tag their
/// diagnostics with <c>SessionId</c> for per-session correlation in Seq/OTLP.
///
/// This restores the correlation that the deleted <c>SessionDiagnosticsContext</c>
/// AsyncLocal used to provide, but through an explicit value carried on the call seam
/// rather than ambient context — ambient state does not flow across the actor mailbox
/// boundary between the session actor and the chat-client pipeline.
///
/// The id rides as a typed CLR property, deliberately NOT in
/// <see cref="ChatOptions.AdditionalProperties"/>: provider clients forward
/// <c>AdditionalProperties</c> verbatim onto the wire (see
/// <c>OpenAiCompatibleChatClient</c>), so a dictionary entry would leak the session id
/// into the LLM request body. A subclass property is invisible to the provider
/// serializers, which only read known fields plus <c>AdditionalProperties</c>.
/// </summary>
public sealed class SessionScopedChatOptions : ChatOptions
{
    /// <summary>The session whose turn this LLM call belongs to. For sub-agent calls this is
    /// the parent session id, so OTEL/Seq can still group the call under the spawning session.</summary>
    public required string SessionId { get; init; }

    /// <summary>For a sub-agent's LLM call, the sub-session id (its run scope
    /// <c>{parent}/subagent/{name}/{runId}</c>). When set, the file-logger partitions these
    /// lines into the sub-agent's own <c>session.log</c> (the local file is keyed by the
    /// sub-session, while <see cref="SessionId"/> keeps OTEL grouping under the parent). Null
    /// for top-level session calls.</summary>
    public string? SubSessionId { get; init; }
}
