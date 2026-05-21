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
/// One per-clause <c>(verb, directory)</c> pair extracted from a sub-agent's
/// invocation. Mirrors the persisted <c>ApprovalEntry</c> shape so the parent
/// session can record folder-scoped grants from the actual paths the sub-agent
/// touched, not just the cwd.
/// </summary>
public sealed record ParentApprovalCandidate(string Verb, string? Directory);

/// <summary>
/// One button on the approval prompt. <paramref name="Key"/> is the wire-stable
/// option key (e.g. <c>approve_once</c>); <paramref name="Label"/> is the
/// human-readable button text.
/// </summary>
public sealed record ParentApprovalOption(string Key, string Label);

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
    /// approvals; <paramref name="candidates"/> preserves the per-clause
    /// <c>(verb, directory)</c> pairs so "Always here" persists with the
    /// actual directory the sub-agent touched. <paramref name="cwd"/> is the
    /// sub-agent's resolved working directory, surfaced in the prompt header
    /// and used by the persistence path. <paramref name="options"/> is the
    /// channel-agnostic button set computed by the approval gate
    /// (<c>BuildApprovalOptions</c>) — implementations MUST emit it verbatim
    /// rather than hardcoding a button list, or persistent grants like
    /// <c>Always anywhere</c> would silently disappear from sub-agent prompts.
    /// </summary>
    Task<ParentApprovalDecision> RequestApprovalAsync(
        ToolCallId callId,
        string toolName,
        string displayText,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> candidateVerbs,
        IReadOnlyList<ParentApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyList<ParentApprovalOption> options,
        bool isMessy,
        CancellationToken ct);
}
