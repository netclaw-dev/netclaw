// -----------------------------------------------------------------------
// <copyright file="IParentApprovalBridge.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Decision returned by a parent session's approval channel in response to a
/// tool approval request from a sub-agent.
/// </summary>
public enum ParentApprovalDecision
{
    ApprovedOnce,
    ApprovedSession,
    ApprovedAlways,
    ApprovedEverywhere,
    Denied,
    TimedOut
}

/// <summary>
/// Bridge that allows sub-agents to route approval requests back to their parent
/// interactive session. Defined in the tools abstraction layer so
/// <see cref="ToolExecutionContext"/> can reference it without depending on actor types.
/// </summary>
public interface IParentApprovalBridge
{
    /// <summary>
    /// Emits an approval request to the parent session and waits for the user's decision.
    /// <paramref name="patterns"/> are the exact blocked units shown in the
    /// prompt and reused for approve-once retries. <paramref name="candidateVerbs"/>
    /// are the verb chains the parent session records for broader-scope
    /// approvals, evaluated against persisted <c>ApprovalEntry</c> records
    /// using the candidate's cwd.
    /// </summary>
    Task<ParentApprovalDecision> RequestApprovalAsync(
        ToolCallId callId,
        string toolName,
        string displayText,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> candidateVerbs,
        bool isMessy,
        CancellationToken ct);
}
