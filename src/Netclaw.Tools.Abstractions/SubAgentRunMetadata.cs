// -----------------------------------------------------------------------
// <copyright file="SubAgentRunMetadata.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Terminal outcome of a subagent run. This is distinct from transport success:
/// a partial run can still return useful text and be eligible for memory review.
/// </summary>
public enum SubAgentRunOutcome
{
    Completed,
    Partial,
    Failed
}

/// <summary>Spawner-generated subagent run id used to correlate logs and notifications.</summary>
public readonly record struct SubAgentRunId(string Value)
{
    public static SubAgentRunId New() => new(Guid.NewGuid().ToString("N"));

    public static explicit operator SubAgentRunId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>Subagent execution scope id used in structured logs.</summary>
public readonly record struct SubAgentScopeId(string Value)
{
    public static explicit operator SubAgentScopeId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>Machine-readable reason for a non-completed subagent outcome.</summary>
public readonly record struct SubAgentOutcomeReason(string Value)
{
    public static readonly SubAgentOutcomeReason MissingAudience = new("missing_audience");
    public static readonly SubAgentOutcomeReason ToolIterationBudgetExhausted = new("tool_iteration_budget_exhausted");
    public static readonly SubAgentOutcomeReason ToolIterationBudgetExceededAfterDisable = new("tool_iteration_budget_exceeded_after_disable");
    public static readonly SubAgentOutcomeReason EmptyFinalResponse = new("empty_final_response");
    public static readonly SubAgentOutcomeReason MalformedFinalOutput = new("malformed_final_output");
    public static readonly SubAgentOutcomeReason ToolExecutionFailed = new("tool_execution_failed");
    public static readonly SubAgentOutcomeReason LlmCallFailed = new("llm_call_failed");
    public static readonly SubAgentOutcomeReason CancelledByParent = new("cancelled_by_parent");
    public static readonly SubAgentOutcomeReason NoSubstantiveOutputTimeout = new("no_substantive_output_timeout");
    public static readonly SubAgentOutcomeReason NoActivityTimeout = new("no_activity_timeout");
    public static readonly SubAgentOutcomeReason ActorStopped = new("actor_stopped");
    public static readonly SubAgentOutcomeReason SpawnUnavailable = new("spawn_unavailable");
    public static readonly SubAgentOutcomeReason NoToolsAvailable = new("no_tools_available");
    public static readonly SubAgentOutcomeReason SpawnError = new("spawn_error");

    public static explicit operator SubAgentOutcomeReason(string value) => new(value);

    public override string ToString() => Value;
}
