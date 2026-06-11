// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

internal sealed class ActiveToolBatchTracker
{
    private readonly HashSet<string> _expectedCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedCallIds = new(StringComparer.Ordinal);

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

    public void Start(IEnumerable<FunctionCallContent> toolCalls)
    {
        ClearExpectedCallIds();
        foreach (var call in toolCalls)
            _expectedCallIds.Add(call.CallId);

        ClearCompletedCallIds();
        ExecutionTaskCompleted = false;
    }

    public void RecordCompleted(string callId)
        => _completedCallIds.Add(callId);

    public void MarkExecutionTaskCompleted()
        => ExecutionTaskCompleted = true;

    public void Clear()
    {
        ClearExpectedCallIds();
        ClearCompletedCallIds();
        ExecutionTaskCompleted = false;
    }

    private void ClearExpectedCallIds()
        => _expectedCallIds.Clear();

    private void ClearCompletedCallIds()
        => _completedCallIds.Clear();
}
