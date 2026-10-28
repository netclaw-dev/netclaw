// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

internal sealed class ActiveToolBatchTracker
{
    private readonly HashSet<string> _expectedCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedCallIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCycleResult> _cycleResults = new(StringComparer.Ordinal);
    private List<SerializableMediaReference> _pendingMedia = [];
    private PreparedToolCycleBatch? _preparedCycleBatch;
    // No journal event holds the nudge, the same as the other system nudges. A snapshot of the history keeps
    // the latest one. Journal replay without that snapshot drops it.
    private (RefusedRepeatAction Stage, string Text)? _repeatNudge;

    private int CompletedCount => _completedCallIds.Count;

    private bool HasAllResults => _expectedCallIds.Count > 0
        && _completedCallIds.Count >= _expectedCallIds.Count;

    private bool CanComplete => ExecutionTaskCompleted
        && HasAllResults;

    private bool ExecutionTaskCompleted { get; set; }

    public bool IsPending(ToolCallId callId)
        => _expectedCallIds.Contains(callId.Value) && !_completedCallIds.Contains(callId.Value);

    public void Start(
        SerializableChatMessage assistantMessage,
        IEnumerable<SerializableChatMessage> existingResults)
    {
        _preparedCycleBatch = null;
        _repeatNudge = null;
        _cycleResults.Clear();
        ClearExpectedCallIds();
        foreach (var call in assistantMessage.ToolCalls)
            _expectedCallIds.Add(call.CallId.Value);

        ClearCompletedCallIds();
        foreach (var result in existingResults)
        {
            if (result.ToolCallId is { } id)
            {
                _completedCallIds.Add(id.Value);
                RestoreOutcome(result);
            }
        }

        ExecutionTaskCompleted = false;
    }

    public void Start(
        IEnumerable<FunctionCallContent> toolCalls,
        PreparedToolCycleBatch? preparedCycleBatch)
    {
        _preparedCycleBatch = preparedCycleBatch;
        _repeatNudge = null;
        _cycleResults.Clear();
        ClearExpectedCallIds();
        foreach (var call in toolCalls)
            _expectedCallIds.Add(call.CallId);

        ClearCompletedCallIds();
        ExecutionTaskCompleted = false;
    }

    public void ResumeDispatch(IReadOnlyList<FunctionCallContent> calls)
    {
        if (calls.Count == 0 || _expectedCallIds.Count == 0 || calls.Any(call => !_expectedCallIds.Contains(call.CallId) || _completedCallIds.Contains(call.CallId)))
            throw new InvalidOperationException("An approval re-drive must contain only unfinished calls of the active batch.");
        if (_completedCallIds.Any(id => !_cycleResults.ContainsKey(id)))
            throw new InvalidOperationException("A recovered sibling result lacks progress receipt facts.");
        _preparedCycleBatch = null;
        ExecutionTaskCompleted = false;
    }

    private void RestoreOutcome(SerializableChatMessage result)
    {
        if (result.ToolCallId is { } id && ToolCycleResult.FromMessage(result) is { } outcome)
            _cycleResults[id.Value] = outcome;
    }

    public SessionState ApplyRecordedResult(SessionState current, SerializableChatMessage result)
    {
        var alreadyRecorded = false;
        if (result.ToolCallId is { } callId)
        {
            _completedCallIds.Add(callId.Value);
            RestoreOutcome(result);
            alreadyRecorded = ParkedToolBatchHistory.HasToolResult(current.History, callId.Value);
        }

        if (alreadyRecorded)
            return current;

        var updated = current with { History = current.History.Add(result) };
        _pendingMedia.AddRange(result.MediaReferences);

        return HasAllResults ? AddMediaNudge(updated, DrainPendingMedia()) : updated;
    }

    public ToolCallId RecordOutcome(ToolCallResult result)
    {
        var message = result.Message;
        var callId = message.ToolCallId
            ?? throw new InvalidOperationException(
                $"Tool-result message for tool '{message.Name ?? "unknown"}' has no ToolCallId.");
        _cycleResults[callId.Value] = ToolCycleResult.FromInvocation(message, result.Receipt, result.FailureCode);
        return callId;
    }

    /// <summary>
    /// The recorded cycle result of a call, or null when the batch holds none. A reader of the batch state
    /// uses this to compare the live path with the restored path.
    /// </summary>
    public ToolCycleResult? CycleResult(ToolCallId callId)
        => _cycleResults.TryGetValue(callId.Value, out var result) ? result : null;

    public BatchCompletion CompleteIfReady(
        SessionState current,
        TurnStateTracker turn)
    {
        if (!CanComplete)
            return BatchCompletion.Pending.Instance;

        var updated = AddMediaNudge(current, DrainPendingMedia());
        if (_preparedCycleBatch is { } prepared)
            turn.ObserveCompleted(ToolCycleSignatureFactory.Complete(prepared, _cycleResults));
        else
            turn.ObserveProgress(_cycleResults.Values, batchComplete: true);

        // Use the completed outcomes, so a success cannot receive a stale progress nudge.
        var nudge = turn.CreateNudgeAfterResults(_repeatNudge, childRun: false);
        // Keep repeat advice until another nudge replaces it. A batch without a cause retires its cause advice.
        if (nudge is not null)
        {
            updated = RemoveLoopNudges(updated, ToolCycleMessages.IsLoopNudge);
            updated = updated.AddSystemNudge(nudge);
        }
        else
        {
            updated = RemoveLoopNudges(updated, turn.CallsSinceProgress == 0
                ? ToolCycleMessages.IsProgressOrCauseNudge : ToolCycleMessages.IsCauseNudge);
        }
        _repeatNudge = null;

        ExecutionTaskCompleted = false;
        turn.RecordToolCompletion(CompletedCount);
        return new BatchCompletion.Ready(updated, CompletedCount);
    }

    public void DiscardPendingMedia() => _pendingMedia.Clear();

    private static SessionState RemoveLoopNudges(SessionState state, Func<string, bool> matches)
    {
        var history = state.History.RemoveAll(message => message.Role == Protocol.ChatRole.User
            && matches(message.Content));
        return ReferenceEquals(history, state.History) ? state : state with { History = history };
    }

    // Start clears this value, so a nudge belongs only to the batch that started before it.
    public void AddNudgeAfterResults((RefusedRepeatAction Stage, string Text) nudge) => _repeatNudge = nudge;

    private IReadOnlyList<SerializableMediaReference> DrainPendingMedia()
    {
        if (_pendingMedia.Count == 0)
            return [];

        var drained = _pendingMedia;
        _pendingMedia = [];
        return drained;
    }

    private static SessionState AddMediaNudge(
        SessionState current,
        IReadOnlyList<SerializableMediaReference> media)
    {
        if (media.Count == 0)
            return current;

        var itemText = media.Count == 1 ? "file" : "files";
        return current.AddSystemNudge(
            $"A tool loaded {media.Count} media {itemText} for model-visible inspection. " +
            "Use the attached media along with the tool result to answer the user.",
            media);
    }

    public void MarkExecutionTaskCompleted()
        => ExecutionTaskCompleted = true;

    public bool HasOnlyPendingDurableApprovals(Func<string, bool> hasDurableApproval)
    {
        if (_expectedCallIds.Count == 0)
            return false;

        var hasUnfinishedApproval = false;
        foreach (var callId in _expectedCallIds)
        {
            if (_completedCallIds.Contains(callId))
                continue;

            if (!hasDurableApproval(callId))
                return false;

            hasUnfinishedApproval = true;
        }

        return hasUnfinishedApproval;
    }

    public void Clear()
    {
        ClearExpectedCallIds();
        ClearCompletedCallIds();
        _preparedCycleBatch = null;
        _repeatNudge = null;
        _cycleResults.Clear();
        ExecutionTaskCompleted = false;
        DiscardPendingMedia();
    }

    private void ClearExpectedCallIds()
        => _expectedCallIds.Clear();

    private void ClearCompletedCallIds()
        => _completedCallIds.Clear();
}

internal abstract record BatchCompletion
{
    internal sealed record Pending : BatchCompletion
    {
        public static readonly Pending Instance = new();
    }

    internal sealed record Ready(SessionState State, int ResultCount) : BatchCompletion
    {
        public ToolBatchNextAction ChooseNext(bool compactionDue)
            => compactionDue ? ToolBatchNextAction.RequestCompaction : ToolBatchNextAction.ContinueModel;
    }

}

internal enum ToolBatchNextAction
{
    ContinueModel,
    RequestCompaction,
    // A person's input waits behind an automation turn. The turn ends quietly at this tool-batch end.
    EndTurn
}
