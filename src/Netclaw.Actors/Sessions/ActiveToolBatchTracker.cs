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
    private string? _repeatNudge;

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
        if (result.ToolOutcomeCategory is not { } category || result.ToolCallId is not { } id)
            return;
        if (!Enum.IsDefined((ToolInvocationOutcomeCategory)category))
            throw new InvalidOperationException("A tool result has an unknown outcome category.");
        _cycleResults[id.Value] = new ToolCycleResult((ToolInvocationOutcomeCategory)category, result.Content)
        {
            PendingJob = result.ToolResultIsPendingJob,
            Synthetic = result.ToolResultIsSynthetic,
            // A record from before the failure fields has no code, so the cause stays unknown. The category
            // alone is never a cause: rebuilding a key from it would report a recurrence that never happened.
            // A code without a source is a corrupt record, so it also stays unknown rather than inventing one.
            FailureKey = result is { ToolFailureSource: { } source, ToolFailureCode: { } code }
                ? new ToolFailureKey(source, code, result.ToolFailureScope)
                : null
        };
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
        _cycleResults[callId.Value] = new ToolCycleResult(
            result.Receipt?.Category ?? ToolInvocationOutcomeCategory.Success,
            message.Content ?? string.Empty)
        {
            PendingJob = result.Receipt is ToolInvocationReceipt.PendingBackgroundJob,
            Synthetic = result.Receipt is ToolInvocationReceipt.Correction { RemediationCode: ToolRemediationCode.BreakToolCycle },
            FailureKey = ToolCycleSignatureFactory.FailureKeyFor(result.FailureCode)
        };
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
        var nudge = ToolCycleMessages.AfterResults(_repeatNudge, turn.EvaluateProgressDecision(),
            turn.CallsSinceProgress, childRun: false);
        // A new nudge replaces the earlier one: equal nudges in a stack give the model no new information.
        // With nothing new to say, keep what history already holds. Removing it unconditionally dropped the
        // advice given on the previous batch, and the model lost its correction on the following request.
        if (nudge is not null)
        {
            updated = RemoveLoopNudges(updated, ToolCycleMessages.IsLoopNudge);
            updated = updated.AddSystemNudge(nudge);
        }
        else if (turn.CallsSinceProgress == 0)
        {
            // The turn is making progress again, so no-progress advice is stale. Repeat advice still names an
            // action to avoid, so it stays until a new nudge replaces it.
            updated = RemoveLoopNudges(updated, ToolCycleMessages.IsProgressNudge);
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
    public void AddNudgeAfterResults(string nudge) => _repeatNudge = nudge;

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
