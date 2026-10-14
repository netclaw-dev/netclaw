// -----------------------------------------------------------------------
// <copyright file="SessionState.ChildRuns.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial record SessionState
{
    public ImmutableDictionary<SubAgentRunId, BackgroundChildRun> ChildRuns { get; init; } = [];

    public SessionState Apply(ChildRunAccepted evt)
    {
        var run = evt.Run;
        if (run.StartKey.SessionId != evt.SessionId || run.OriginalContext.SessionId != evt.SessionId ||
            run.StartKey.TurnId != run.OriginalContext.TurnId || run.Lifecycle is not ChildRunLifecycle.Accepted ||
            string.IsNullOrWhiteSpace(run.RunId.Value) || string.IsNullOrWhiteSpace(run.ArgumentsDigest))
            throw new InvalidOperationException("Child acceptance has invalid identity or authority.");
        if (ChildRuns.ContainsKey(run.RunId) || ChildRuns.Values.Any(existing => existing.StartKey == run.StartKey))
            throw new InvalidOperationException("Child acceptance repeats a durable start identity.");
        if (run.StartKey is ChildRunStartKey.Tool tool &&
            (evt.AcceptanceResult is not { Role: ChatRole.Tool } result || result.ToolCallId != tool.CallId ||
             !History.Any(message => message.ToolCalls.Any(call => call.CallId == tool.CallId))))
            throw new InvalidOperationException("Child acceptance must close its original tool call.");
        if (run.StartKey is ChildRunStartKey.Slash && evt.AcceptanceResult is not null)
            throw new InvalidOperationException("A slash start cannot create an initial tool result.");
        return this with
        {
            ChildRuns = ChildRuns.Add(run.RunId, run),
            History = evt.AcceptanceResult is { } accepted ? History.Add(accepted) : History
        };
    }

    public SessionState Apply(ChildRunEvent evt)
    {
        if (!ChildRuns.TryGetValue(evt.RunId, out var run) || run.OriginalContext.SessionId != evt.SessionId)
            throw new InvalidOperationException("Child event does not name an accepted run.");
        ChildRunLifecycle next = (run.Lifecycle, evt) switch
        {
            (ChildRunLifecycle.Accepted current, ChildRunEvent.Checkpointed update)
                when update.Checkpoint.CompletedRound > (current.Checkpoint?.CompletedRound ?? -1)
                => new ChildRunLifecycle.Accepted(update.Checkpoint),
            (ChildRunLifecycle.Accepted current, ChildRunEvent.CancellationRequested)
                => new ChildRunLifecycle.Cancelling(current.Checkpoint),
            (ChildRunLifecycle.Cancelling, ChildRunEvent.CancellationRequested) => run.Lifecycle,
            (ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling, ChildRunEvent.TerminalRecorded terminal)
                when terminal.Terminal.Result.RunId == run.RunId && terminal.Terminal.Result.ScopeId == run.ScopeId
                => new ChildRunLifecycle.Terminal(terminal.Terminal),
            (ChildRunLifecycle.Terminal terminal, ChildRunEvent.ResultPrepared prepared)
                when IsCanonicalChildPair(run, prepared.Call, prepared.Result)
                => new ChildRunLifecycle.Prepared(new ChildPreparedResult(terminal.Result, prepared.Call, prepared.Result)),
            _ => throw new InvalidOperationException("Child event is invalid for its current lifecycle.")
        };
        return this with { ChildRuns = ChildRuns.SetItem(run.RunId, run with { Lifecycle = next }) };
    }

    public SessionState Apply(InputClosed evt)
        => CommitChildContinuation(evt.ChildContinuation, close: true).CloseInputs(evt.InputIds);

    internal SessionState CommitChildContinuation(ChildContinuation? continuation, bool close)
    {
        if (continuation is null)
            return this;
        if (!ChildRuns.TryGetValue(continuation.RunId, out var run))
            throw new InvalidOperationException("A parent decision names an unknown child result.");
        if (run.Lifecycle is ChildRunLifecycle.Closed closed && closed.DeliveryCallId == continuation.DeliveryCallId)
            return this;
        var delivery = run.Lifecycle switch
        {
            ChildRunLifecycle.Prepared ready => ready.Delivery,
            ChildRunLifecycle.Consumed consumed => consumed.Delivery,
            _ => throw new InvalidOperationException("A parent decision requires a prepared child result.")
        };
        if (delivery.Result.ToolCallId != continuation.DeliveryCallId)
            throw new InvalidOperationException("A parent decision names a stale child result.");
        var history = run.Lifecycle is ChildRunLifecycle.Prepared
            ? History.Add(delivery.Call).Add(delivery.Result)
            : History;
        ChildRunLifecycle next = close
            ? new ChildRunLifecycle.Closed(continuation.DeliveryCallId,
                delivery.Terminal.Result.Outcome, delivery.Terminal.Result.OutcomeReason)
            : new ChildRunLifecycle.Consumed(delivery);
        return this with { History = history, ChildRuns = ChildRuns.SetItem(run.RunId, run with { Lifecycle = next }) };
    }

    private static bool IsCanonicalChildPair(BackgroundChildRun run, SerializableChatMessage call, SerializableChatMessage result)
        => call is { Role: ChatRole.Assistant, ToolCalls.Count: 1 } &&
           result is { Role: ChatRole.Tool, ToolCallId: { } resultId } &&
           call.ToolCalls[0].CallId == resultId &&
           call.ToolCalls[0].Name.Value == "child_run_result" &&
           result.Name == "child_run_result" &&
           (run.StartKey is not ChildRunStartKey.Tool original || original.CallId != resultId);
}
