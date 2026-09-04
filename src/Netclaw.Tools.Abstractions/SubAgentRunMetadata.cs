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
public readonly record struct SubAgentRunId
{
    /// <summary>Creates a validated run identifier.</summary>
    public SubAgentRunId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "A subagent run identifier can contain letters, numbers, hyphens, and underscores only.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the validated identifier.</summary>
    public string Value { get; }

    /// <summary>Creates a new random run identifier.</summary>
    public static SubAgentRunId New() => new(Guid.NewGuid().ToString("N"));

    /// <summary>Converts a string into a validated run identifier.</summary>
    public static explicit operator SubAgentRunId(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Subagent execution scope id used in structured logs.</summary>
public readonly record struct SubAgentScopeId(string Value)
{
    /// <summary>Converts a string into a child execution scope identifier.</summary>
    public static explicit operator SubAgentScopeId(string value) => new(value);

    /// <summary>Extracts the final run identifier from a composite child scope.</summary>
    public bool TryGetRunId(out SubAgentRunId runId)
    {
        var marker = Value.LastIndexOf("/subagent/", StringComparison.Ordinal);
        var separator = Value.LastIndexOf('/');
        if (marker < 0 || separator <= marker + "/subagent/".Length || separator == Value.Length - 1)
        {
            runId = default;
            return false;
        }

        runId = new SubAgentRunId(Value[(separator + 1)..]);
        return true;
    }

    /// <inheritdoc />
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
