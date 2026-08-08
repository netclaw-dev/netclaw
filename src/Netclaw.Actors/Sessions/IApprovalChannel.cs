// -----------------------------------------------------------------------
// <copyright file="IApprovalChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Collections.Generic;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Approval decision from the user in response to a <see cref="SessionProtocol.ToolInteractionRequest"/>.
/// </summary>
public enum ApprovalDecision
{
    /// <summary>Approved for the current blocked tool call retry only.</summary>
    ApprovedOnce,

    /// <summary>Approved for the current session/thread only.</summary>
    ApprovedSession,

    /// <summary>
    /// Approved persistently with folder scope: writes a
    /// <c>(verb, prompt's cwd)</c> entry to <c>tool-approvals.json</c>.
    /// Future invocations of the same verb under the same directory tree
    /// auto-approve; the same verb in a different cwd still prompts.
    /// </summary>
    ApprovedAlways,

    /// <summary>
    /// Approved persistently as a global wildcard: writes a
    /// <c>(verb, null)</c> entry to <c>tool-approvals.json</c>. Future
    /// invocations of the same verb in any cwd auto-approve. Used for
    /// scheduled/unattended tasks where the cwd will vary across firings.
    /// </summary>
    ApprovedEverywhere,

    /// <summary>User denied the request.</summary>
    Denied,

    /// <summary>No response received within the timeout window.</summary>
    TimedOut
}

/// <summary>
/// Extensions over <see cref="ApprovalDecision"/>.
/// </summary>
public static class ApprovalDecisionExtensions
{
    /// <summary>
    /// True when the decision grants execution (any approve scope) rather than
    /// Denied or TimedOut. Every "the user approved" branch — the live pipeline
    /// retry, the cold re-drive plan, and the sub-agent loop — must classify the
    /// approve scopes identically, so route them through this one predicate
    /// instead of duplicating the scope list (a missed site reintroduces the
    /// "approved command still fails" bug for the new scope).
    /// </summary>
    public static bool IsApprovalGrant(this ApprovalDecision decision)
        => decision is ApprovalDecision.ApprovedOnce
            or ApprovalDecision.ApprovedSession
            or ApprovalDecision.ApprovedAlways
            or ApprovalDecision.ApprovedEverywhere;
}

/// <summary>
/// Bridge between the tool execution pipeline (thread pool) and the session actor
/// (mailbox). Allows tool tasks to block awaiting user approval while the actor
/// remains responsive to incoming messages.
/// </summary>
internal interface IApprovalChannel
{
    /// <summary>
    /// Waits for an approval decision for the given tool call. Blocks the calling
    /// task (on thread pool) without consuming a thread. Returns <see cref="ApprovalDecision.TimedOut"/>
    /// if no decision arrives within <paramref name="timeout"/>.
    /// </summary>
    Task<ApprovalDecision> WaitForApprovalAsync(ToolCallId callId, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Atomically claims a pending approval request so no later response can use
    /// the same prompt while the session persists any required grant state.
    /// Returns false when the prompt is no longer backed by a live wait.
    /// </summary>
    bool TryClaim(ToolCallId callId, out ClaimedApprovalWait wait);

    /// <summary>
    /// Completes a pending approval request. Called by tests and simple callers
    /// that do not need a separate claim/persist/complete sequence.
    /// </summary>
    bool Complete(ToolCallId callId, ApprovalDecision decision);
}

/// <summary>
/// A live approval wait that has been removed from the pending map but has not
/// yet been completed. This lets the session claim the user response before
/// writing durable grant state, closing stale-click races.
/// </summary>
internal sealed class ClaimedApprovalWait
{
    private readonly TaskCompletionSource<ApprovalDecision> _completion;

    public ClaimedApprovalWait(TaskCompletionSource<ApprovalDecision> completion)
        => _completion = completion;

    public bool Complete(ApprovalDecision decision)
        => _completion.TrySetResult(decision);
}

/// <summary>
/// Default implementation using a dictionary of <see cref="TaskCompletionSource{T}"/>
/// keyed by call ID.
/// </summary>
internal sealed class ApprovalChannel : IApprovalChannel
{
    private readonly ConcurrentDictionary<ToolCallId, TaskCompletionSource<ApprovalDecision>> _pending = new();

    public async Task<ApprovalDecision> WaitForApprovalAsync(ToolCallId callId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(callId, tcs))
            throw new InvalidOperationException($"Approval wait for call '{callId}' is already pending.");

        try
        {
            var timeoutTask = timeout == Timeout.InfiniteTimeSpan
                ? Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                : Task.Delay(timeout, CancellationToken.None);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask, cancellationTask);

            if (completed == tcs.Task)
                return await tcs.Task;

            if (completed == cancellationTask)
                throw new OperationCanceledException(ct);

            return ApprovalDecision.TimedOut;
        }
        finally
        {
            TryRemoveExact(callId, tcs);
        }
    }

    public bool TryClaim(ToolCallId callId, out ClaimedApprovalWait wait)
    {
        if (_pending.TryRemove(callId, out var tcs))
        {
            wait = new ClaimedApprovalWait(tcs);
            return true;
        }

        wait = null!;
        return false;
    }

    public bool Complete(ToolCallId callId, ApprovalDecision decision)
        => TryClaim(callId, out var wait) && wait.Complete(decision);

    private bool TryRemoveExact(ToolCallId callId, TaskCompletionSource<ApprovalDecision> tcs)
    {
        var pair = new KeyValuePair<ToolCallId, TaskCompletionSource<ApprovalDecision>>(callId, tcs);
        return ((ICollection<KeyValuePair<ToolCallId, TaskCompletionSource<ApprovalDecision>>>)_pending).Remove(pair);
    }
}
