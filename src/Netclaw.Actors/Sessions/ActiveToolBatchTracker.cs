// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

internal sealed class ActiveToolBatchTracker
{
    private readonly HashSet<string> _expectedCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedCallIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCycleResult> _cycleResults = new(StringComparer.Ordinal);
    private PreparedToolCycleBatch? _preparedCycleBatch;

    private readonly HashSet<string> _refusedCallIds = new(StringComparer.Ordinal);
    public bool MissingReceipt { get; private set; }

    public int CompletedCount => _completedCallIds.Count;

    public bool HasAllResults => _expectedCallIds.Count > 0
        && _completedCallIds.Count >= _expectedCallIds.Count;

    public bool CanComplete => ExecutionTaskCompleted
        && HasAllResults;

    private bool ExecutionTaskCompleted { get; set; }

    public void Start(
        SerializableChatMessage assistantMessage,
        IEnumerable<SerializableChatMessage> existingResults)
    {
        _preparedCycleBatch = null;
        _cycleResults.Clear();
        _refusedCallIds.Clear();
        MissingReceipt = false;
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
        _cycleResults.Clear();
        _refusedCallIds.Clear();
        MissingReceipt = false;
        ClearExpectedCallIds();
        foreach (var call in toolCalls)
            _expectedCallIds.Add(call.CallId);

        ClearCompletedCallIds();
        ExecutionTaskCompleted = false;
    }

    public void RecordCompleted(string callId)
        => _completedCallIds.Add(callId);

    public void RecordCycleResult(
        string callId,
        ToolInvocationReceipt? receipt,
        string modelVisibleText)
    {
        if (receipt is null)
        {
            MissingReceipt = true;
            return;
        }
        _cycleResults[callId] = new ToolCycleResult(receipt.Category, modelVisibleText)
        { PendingJob = receipt is ToolInvocationReceipt.PendingBackgroundJob };
    }

    public void MarkRefused(IEnumerable<string> callIds)
        => _refusedCallIds.UnionWith(callIds);

    public CompletedToolCycleIteration? GetCompletedCycle()
    {
        if (_preparedCycleBatch is null || MissingReceipt)
            return null;
        var actual = _preparedCycleBatch.Calls.Where(call => !_refusedCallIds.Contains(call.CallId.Value)).ToArray();
        if (actual.Length == 0)
            return null;
        var results = _cycleResults.Where(pair => !_refusedCallIds.Contains(pair.Key)).ToDictionary();
        return ToolCycleSignatureFactory.Complete(new PreparedToolCycleBatch(_preparedCycleBatch.Action, actual), results)
            with { RecordAdjacent = _refusedCallIds.Count == 0 && !results.Values.Any(result => result.PendingJob) };
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
        _cycleResults.Clear();
        _refusedCallIds.Clear();
        MissingReceipt = false;
        ExecutionTaskCompleted = false;
    }

    private void ClearExpectedCallIds()
        => _expectedCallIds.Clear();

    private void ClearCompletedCallIds()
        => _completedCallIds.Clear();
}
