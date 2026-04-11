using System.Collections.Concurrent;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Approval decision from the user in response to a <see cref="Protocol.ToolInteractionRequest"/>.
/// </summary>
public enum ApprovalDecision
{
    /// <summary>Approved for the current blocked tool call retry only.</summary>
    ApprovedOnce,

    /// <summary>Approved for the current session/thread only.</summary>
    ApprovedSession,

    /// <summary>Approved permanently (persisted to tool-approvals.json).</summary>
    ApprovedAlways,

    /// <summary>User denied the request.</summary>
    Denied,

    /// <summary>No response received within the timeout window.</summary>
    TimedOut
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
    Task<ApprovalDecision> WaitForApprovalAsync(string callId, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Completes a pending approval request. Called by the session actor when a
    /// <see cref="Protocol.ToolInteractionResponse"/> message arrives.
    /// </summary>
    void Complete(string callId, ApprovalDecision decision);
}

/// <summary>
/// Default implementation using a dictionary of <see cref="TaskCompletionSource{T}"/>
/// keyed by call ID.
/// </summary>
internal sealed class ApprovalChannel : IApprovalChannel
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pending = new();

    public async Task<ApprovalDecision> WaitForApprovalAsync(string callId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _pending.GetOrAdd(callId, _ => new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));

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
            _pending.TryRemove(callId, out _);
        }
    }

    public void Complete(string callId, ApprovalDecision decision)
    {
        if (_pending.TryGetValue(callId, out var tcs))
            tcs.TrySetResult(decision);
    }
}
