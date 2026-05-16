// -----------------------------------------------------------------------
// <copyright file="SubAgentProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Defines a subagent's identity: system prompt, tools, and model.
/// Can be constructed from built-in definitions or authored dynamically.
/// </summary>
public sealed record SubAgentDefinition
{
    /// <summary>Human-readable name for logging and observability.</summary>
    public required AgentName Name { get; init; }

    /// <summary>System prompt for the subagent's LLM context.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Tools available to the subagent during execution.</summary>
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
/// Timeout budget for a sub-agent run. The three inactivity budgets mirror the
/// parent session's <see cref="Configuration.SessionConfig"/> values and reset on
/// activity (streaming deltas, tool transitions); <see cref="AbsoluteBackstop"/>
/// is a single hard wall-clock cap on the whole run regardless of activity.
/// </summary>
public sealed record SubAgentTimeoutBudget
{
    /// <summary>Inactivity budget awaiting the first streaming delta of an LLM call.</summary>
    public required TimeSpan PrefillTimeout { get; init; }

    /// <summary>Inactivity budget between consecutive streaming deltas.</summary>
    public required TimeSpan FirstTokenTimeout { get; init; }

    /// <summary>Inactivity budget covering a single tool batch.</summary>
    public required TimeSpan ToolExecutionTimeout { get; init; }

    /// <summary>Hard wall-clock cap on the entire sub-agent run.</summary>
    public required TimeSpan AbsoluteBackstop { get; init; }

    /// <summary>
    /// Degenerate budget derived from a single flat timeout — used when a caller
    /// supplies only <see cref="RunSubAgent.Timeout"/> and no explicit budget.
    /// Every phase collapses to the one value.
    /// </summary>
    public static SubAgentTimeoutBudget FromLegacyTimeout(TimeSpan timeout) => new()
    {
        PrefillTimeout = timeout,
        FirstTokenTimeout = timeout,
        ToolExecutionTimeout = timeout,
        AbsoluteBackstop = timeout
    };
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

    /// <summary>Wall-clock timeout set by the caller.</summary>
    public required TimeSpan Timeout { get; init; }

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

    /// <summary>
    /// Parent session's session directory snapshot when the subagent was spawned.
    /// </summary>
    public string? ParentSessionDirectory { get; init; }

    /// <summary>
    /// Parent session's project directory snapshot when the subagent was spawned.
    /// </summary>
    public string? ParentProjectDirectory { get; init; }

    /// <summary>
    /// Parent session's approval bridge. When provided, the sub-agent can route
    /// approval requests back to the interactive user instead of auto-denying.
    /// </summary>
    public IParentApprovalBridge? ApprovalBridge { get; init; }

    /// <summary>
    /// Per-operation inactivity budgets plus the absolute backstop. When null the
    /// sub-agent derives a degenerate budget from <see cref="Timeout"/>.
    /// </summary>
    public SubAgentTimeoutBudget? TimeoutBudget { get; init; }

    /// <summary>
    /// Parent session actor that receives <see cref="SubAgentHeartbeat"/> liveness
    /// signals while this sub-agent runs. Null for standalone runs with no parent
    /// watchdog to refresh.
    /// </summary>
    public IActorRef? HeartbeatSink { get; init; }

    /// <summary>Spawn-unique run id, echoed back in heartbeats for correlation.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// The parent's <c>ProcessingWatchdog</c> operation id for the <c>spawn_agent</c>
    /// tool batch, echoed in every <see cref="SubAgentHeartbeat"/> so a stale
    /// heartbeat cannot refresh the wrong parent operation.
    /// </summary>
    public long ParentWatchdogOpId { get; init; }
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

/// <summary>Phase a sub-agent reports in a <see cref="SubAgentHeartbeat"/>.</summary>
public enum SubAgentHeartbeatPhase
{
    /// <summary>The sub-agent's LLM call is actively streaming.</summary>
    LlmStreaming,

    /// <summary>The sub-agent has dispatched a tool batch.</summary>
    ToolDispatch,

    /// <summary>The sub-agent's tool batch completed.</summary>
    ToolComplete
}

/// <summary>
/// Liveness signal sent from a running <see cref="SubAgentActor"/> to its parent
/// session so the parent can keep the <c>spawn_agent</c> tool-execution watchdog
/// refreshed for as long as the sub-agent is making progress.
/// </summary>
public sealed record SubAgentHeartbeat : INoSerializationVerificationNeeded
{
    /// <summary>The reporting sub-agent's name.</summary>
    public required AgentName AgentName { get; init; }

    /// <summary>Spawn-unique run id, correlating this heartbeat to one spawn.</summary>
    public required string RunId { get; init; }

    /// <summary>
    /// The parent session's <c>ProcessingWatchdog</c> operation id captured when the
    /// <c>spawn_agent</c> tool batch was armed. The parent refreshes its watchdog
    /// only while this still matches its current operation.
    /// </summary>
    public required long ParentWatchdogOpId { get; init; }

    /// <summary>What the sub-agent was doing when it emitted this heartbeat.</summary>
    public SubAgentHeartbeatPhase Phase { get; init; }

    /// <summary>Input tokens for the sub-agent's most recent completed LLM call, when known.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Output tokens for the sub-agent's most recent completed LLM call, when known.</summary>
    public long? OutputTokens { get; init; }
}

