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

    public int BatchSize { get; private set; }

    public bool HasReachedDispatch { get; private set; }

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
        BatchSize = assistantMessage.ToolCalls.Count;
        HasReachedDispatch = false;
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
        var calls = toolCalls.ToArray();
        _preparedCycleBatch = preparedCycleBatch;
        _cycleResults.Clear();
        BatchSize = calls.Length;
        HasReachedDispatch = false;
        ClearExpectedCallIds();
        foreach (var call in calls)
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

    public void MarkDispatched()
    {
        if (BatchSize == 0)
            throw new InvalidOperationException("A tool batch must be active before dispatch is marked.");

        HasReachedDispatch = true;
    }

    public void Clear()
    {
        ClearExpectedCallIds();
        ClearCompletedCallIds();
        _preparedCycleBatch = null;
        _cycleResults.Clear();
        BatchSize = 0;
        HasReachedDispatch = false;
        ExecutionTaskCompleted = false;
    }

    private void ClearExpectedCallIds()
        => _expectedCallIds.Clear();

    private void ClearCompletedCallIds()
        => _completedCallIds.Clear();
}
