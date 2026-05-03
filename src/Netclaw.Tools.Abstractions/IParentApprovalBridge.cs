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
    /// </summary>
    Task<ParentApprovalDecision> RequestApprovalAsync(
        ToolCallId callId,
        string toolName,
        string displayText,
        IReadOnlyList<string> unapprovedPatterns,
        CancellationToken ct);
}
