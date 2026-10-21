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
                _completedCallIds.Add(id.Value);
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

    public SessionState ApplyRecordedResult(SessionState current, SerializableChatMessage result)
    {
        var alreadyRecorded = false;
        if (result.ToolCallId is { } callId)
        {
            _completedCallIds.Add(callId.Value);
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
            Synthetic = result.Receipt is ToolInvocationReceipt.Correction { RemediationCode: ToolRemediationCode.BreakToolCycle }
        };
        return callId;
    }

    public BatchCompletion CompleteIfReady(
        SessionState current,
        TurnStateTracker turn)
    {
        if (!CanComplete)
            return BatchCompletion.Pending.Instance;

        var updated = AddMediaNudge(current, DrainPendingMedia());
        // The repeat nudge follows every result of the batch, so the next model request ends with it. It replaces
        // the earlier repeat nudge: equal nudges in a stack give the model no new information.
        if (_repeatNudge is { } repeatNudge)
        {
            updated = updated.ReplaceSystemNudge(ToolCycleMessages.RepeatNudgeStart, repeatNudge);
            _repeatNudge = null;
        }

        if (_preparedCycleBatch is { } prepared)
            turn.ObserveCompleted(ToolCycleSignatureFactory.Complete(prepared, _cycleResults));

        turn.RecordToolCompletion(CompletedCount);
        return new BatchCompletion.Ready(updated, CompletedCount);
    }

    public void DiscardPendingMedia() => _pendingMedia.Clear();

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
    RequestCompaction
}
