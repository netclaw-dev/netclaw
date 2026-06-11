// -----------------------------------------------------------------------
// <copyright file="SubAgentProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Defines a subagent's identity: system prompt, resolved runtime tools, and model.
/// Can be constructed from built-in definitions or authored dynamically.
/// </summary>
public sealed record SubAgentDefinition
{
    /// <summary>Human-readable name for logging and observability.</summary>
    public required AgentName Name { get; init; }

    /// <summary>System prompt for the subagent's LLM context.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Runtime tools available to the subagent after audience-policy filtering.</summary>
    public required IReadOnlyList<INetclawTool> Tools { get; init; }

    /// <summary>
    /// Model role to resolve via <see cref="Configuration.IChatClientProvider"/>.
    /// Defaults to <see cref="Configuration.ModelRole.Compaction"/> (cheaper/faster model).
    /// </summary>
    public Configuration.ModelRole ModelRole { get; init; } = Configuration.ModelRole.Compaction;

    /// <summary>
    /// Whether successful free-form output should be converted into structured findings.
    /// </summary>
    public bool EmitStructuredFindings { get; init; }

    /// <summary>
    /// Optional project-scoped identity content inherited from the parent session.
    /// </summary>
    public string? ProjectInstructions { get; init; }
}

/// <summary>
/// Message sent to a <see cref="SubAgentActor"/> to begin execution.
/// </summary>
public sealed record RunSubAgent : INoSerializationVerificationNeeded
{
    /// <summary>The task for the subagent to perform (becomes part of the user message).</summary>
    public required string Task { get; init; }

    /// <summary>
    /// Optional per-invocation background context from the parent session. When present,
    /// it is prefixed onto the subagent's first user message as a "Context:" block so the
    /// agent's static system prompt (loaded from disk) remains reproducible.
    /// </summary>
    public string? RuntimeContext { get; init; }

    /// <summary>
    /// Inter-delta inactivity budget: the maximum gap between streaming deltas
    /// once the model has started responding, and the general inactivity budget
    /// for the tool loop. The watchdog promotes to this budget on the first
    /// substantive delta.
    /// </summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>
    /// Generous wait-for-first-delta budget covering queue wait and cold prefill.
    /// The watchdog starts on this budget and content-free keepalives refresh it,
    /// so a healthy-but-slow self-hosted prefill is not killed. When unset
    /// (<see cref="TimeSpan.Zero"/>), the sub-agent defaults to the same 1800s
    /// budget as the main session path rather than collapsing to <see cref="Timeout"/>.
    /// </summary>
    public TimeSpan PrefillTimeout { get; init; }

    /// <summary>
    /// Hard ceiling on time without substantive output. Reset only by real
    /// streaming tokens — content-free keepalives never extend it — so a backend
    /// that heartbeats forever without producing a token is killed once this
    /// elapses. <see cref="TimeSpan.Zero"/> (unset) leaves the call bounded only by
    /// the liveness watchdog; the spawner always populates it from
    /// <see cref="Configuration.SubAgentConfig.NoProgressTimeoutSeconds"/>.
    /// </summary>
    public TimeSpan NoProgressTimeout { get; init; }

    /// <summary>
    /// Cancellation token from the calling tool execution. Used to stop the
    /// subagent promptly when the parent turn is cancelled or times out.
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// Optional execution scope ID used for session-scoped tool routing.
    /// When omitted, the subagent runtime assigns a unique transient scope.
    /// </summary>
    public string? SessionScopeId { get; init; }

    /// <summary>
    /// Trust audience inherited from the spawning session. A parsed
    /// <see cref="TrustAudience"/> — the sub-agent actor rejects a spawn with no
    /// audience rather than defaulting it.
    /// </summary>
    public TrustAudience? Audience { get; init; }

    public TrustBoundary? Boundary { get; init; }

    public string? ChannelType { get; init; }

    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }

    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }

    /// <summary>
    /// Input modalities supported by the model selected for this sub-agent run.
    /// Tools use this to decide whether model-visible media handoff is allowed.
    /// </summary>
    public ModelModality ModelInputModalities { get; init; } = ModelModality.Text;

    /// <summary>
    /// Parent session's session directory snapshot when the subagent was spawned.
    /// </summary>
    public string? ParentSessionDirectory { get; init; }

    /// <summary>
    /// Parent session's project directory snapshot when the subagent was spawned.
    /// </summary>
    public string? ParentProjectDirectory { get; init; }

    /// <summary>
    /// Snapshot of the parent's <c>ToolExecutionContext.ResolveShellCwd(null)</c>
    /// at spawn time. Seeds the child's <c>InheritedCwd</c>. Null when the
    /// parent itself had no resolvable cwd.
    /// </summary>
    public string? ParentCwd { get; init; }

    /// <summary>
    /// Parent session's approval bridge. When provided, the sub-agent can route
    /// approval requests back to the interactive user instead of auto-denying.
    /// </summary>
    public IParentApprovalBridge? ApprovalBridge { get; init; }

    /// <summary>
    /// Optional sink for liveness/progress activity emitted while the sub-agent
    /// runs. Streaming <c>spawn_agent</c> calls provide this so the parent's
    /// per-call watchdog sees a long-but-healthy run as alive. Non-streaming
    /// callers leave it null because no parent stream is observing activity.
    /// </summary>
    public ChannelWriter<ToolActivityUpdate>? ActivitySink { get; init; }
}

/// <summary>
/// Result returned by a <see cref="SubAgentActor"/> when execution completes.
/// </summary>
public sealed record SubAgentResult : INoSerializationVerificationNeeded
{
    /// <summary>Whether the subagent completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>The subagent's final text output (or error message on failure).</summary>
    public required string Output { get; init; }

    /// <summary>Name of the subagent that produced this result.</summary>
    public required AgentName AgentName { get; init; }

    /// <summary>
    /// Structured findings returned to the owning session for policy and checkpoint review.
    /// </summary>
    public List<SubAgentFinding> Findings { get; init; } = [];

    /// <summary>
    /// Total number of structured findings returned before parent-session review.
    /// </summary>
    public int FindingsCount { get; init; }
}
