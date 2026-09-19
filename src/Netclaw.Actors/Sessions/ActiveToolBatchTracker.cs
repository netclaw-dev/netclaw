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

    public long Generation { get; private set; }

    public Task? ExecutionTask { get; private set; }

    public bool RestartStopRequested { get; private set; }

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
        ResetExecution();
        _preparedCycleBatch = null;
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
        ResetExecution();
        _preparedCycleBatch = preparedCycleBatch;
        _cycleResults.Clear();
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
        ToolInvocationOutcomeCategory category,
        string modelVisibleText)
        => _cycleResults[callId] = new ToolCycleResult(category, modelVisibleText);

    public CompletedToolCycleIteration? GetCompletedCycle()
        => _preparedCycleBatch is null
            ? null
            : ToolCycleSignatureFactory.Complete(_preparedCycleBatch, _cycleResults);

    public void MarkExecutionTaskCompleted()
        => ExecutionTaskCompleted = true;

    public void SetExecutionTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ExecutionTask = task;
    }

    public bool CanStopForDurableApprovals(Func<string, bool> hasDurableApproval)
    {
        if (_expectedCallIds.Count == 0 || ExecutionTask is null
            || ExecutionTask.IsCompleted || RestartStopRequested)
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

    public void MarkRestartStopRequested()
        => RestartStopRequested = true;

    public void Clear()
    {
        ResetExecution();
        ClearExpectedCallIds();
        ClearCompletedCallIds();
        _preparedCycleBatch = null;
        _cycleResults.Clear();
        ExecutionTaskCompleted = false;
    }

    private void ResetExecution()
    {
        Generation++;
        ExecutionTask = null;
        RestartStopRequested = false;
    }

    private void ClearExpectedCallIds()
        => _expectedCallIds.Clear();

    private void ClearCompletedCallIds()
        => _completedCallIds.Clear();
}
