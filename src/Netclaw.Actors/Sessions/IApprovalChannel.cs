// -----------------------------------------------------------------------
// <copyright file="IApprovalChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Collections.Generic;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Bridge between the tool execution pipeline (thread pool) and the session actor
/// (mailbox). Allows tool tasks to block awaiting user approval while the actor
/// remains responsive to incoming messages.
/// </summary>
internal interface IApprovalChannel
{
    /// <summary>
    /// Waits for the operator's answer for the given tool call. Blocks the calling
    /// task (on thread pool) without consuming a thread. Returns a timeout refusal
    /// if no decision arrives within <paramref name="timeout"/>.
    /// </summary>
    Task<ConsentAnswer> WaitForApprovalAsync(ToolCallId callId, TimeSpan timeout, CancellationToken ct);

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
    bool Complete(ToolCallId callId, ConsentAnswer answer);
}

/// <summary>
/// A live approval wait that has been removed from the pending map but has not
/// yet been completed. This lets the session claim the user response before
/// writing durable grant state, closing stale-click races.
/// </summary>
internal sealed class ClaimedApprovalWait
{
    private readonly TaskCompletionSource<ConsentAnswer> _completion;

    public ClaimedApprovalWait(TaskCompletionSource<ConsentAnswer> completion)
        => _completion = completion;

    public bool Complete(ConsentAnswer answer)
        => _completion.TrySetResult(answer);
}

/// <summary>
/// Default implementation using a dictionary of <see cref="TaskCompletionSource{T}"/>
/// keyed by call ID.
/// </summary>
internal sealed class ApprovalChannel : IApprovalChannel
{
    private readonly ConcurrentDictionary<ToolCallId, TaskCompletionSource<ConsentAnswer>> _pending = new();

    public async Task<ConsentAnswer> WaitForApprovalAsync(ToolCallId callId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
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

            return ConsentAnswer.TimedOut;
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

    public bool Complete(ToolCallId callId, ConsentAnswer answer)
        => TryClaim(callId, out var wait) && wait.Complete(answer);

    private bool TryRemoveExact(ToolCallId callId, TaskCompletionSource<ConsentAnswer> tcs)
    {
        var pair = new KeyValuePair<ToolCallId, TaskCompletionSource<ConsentAnswer>>(callId, tcs);
        return ((ICollection<KeyValuePair<ToolCallId, TaskCompletionSource<ConsentAnswer>>>)_pending).Remove(pair);
    }
}
