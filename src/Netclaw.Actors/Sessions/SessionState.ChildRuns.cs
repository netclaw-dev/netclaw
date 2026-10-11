// -----------------------------------------------------------------------
// <copyright file="SessionState.ChildRuns.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial record SessionState
{
    internal SessionState SettleAbandonedChildStartBatches(ToolBatchAbandoned evt)
    {
        if (LoopAdmission is not { } admission)
            return this;
        var closedCalls = evt.ToolResults.Where(static result => result.Role == Protocol.ChatRole.Tool)
            .Select(static result => result.ToolCallId?.Value).ToHashSet(StringComparer.Ordinal);
        if (!admission.Calls.All(call => closedCalls.Contains(call.CallId)
                || LoopObservations.Any(observation => observation.CallId == call.CallId)))
            return this;
        var runs = ChildRuns;
        foreach (var (id, run) in runs)
        {
            if (run.StartKey is ChildRunStartKey.Tool tool
                && admission.Calls.Any(call => call.CallId == tool.CallId.Value))
                runs = runs.SetItem(id, run with { StartBatchSettled = true });
        }
        // Abandonment closes unknown work. It creates no receipt or detector observation.
        return this with { ChildRuns = runs };
    }

    public SessionState ApplyChildApproval(ToolApprovalRequested request)
    {
        if (request.SourceChildRunId is not { } runId || !ChildRuns.TryGetValue(runId, out var run)
            || run.Terminal is not null || run.CancellationRequestedAtMs is not null || run.DispatchClosedAtMs is not null
            || run.Approvals.Any(approval => approval.Request.CallId == request.CallId))
            throw new InvalidDataException("A child approval has no open accepted run.");
        var approval = new ChildRunApproval(request, null);
        run.ValidateApproval(approval);
        return this with { ChildRuns = ChildRuns.SetItem(runId, run with { Approvals = [..run.Approvals, approval] }) };
    }

    public SessionState ApplyChildApproval(ToolApprovalResolved resolution)
    {
        if (resolution.SourceChildRunId is not { } runId || !ChildRuns.TryGetValue(runId, out var run))
            throw new InvalidDataException("A child approval resolution has no accepted run.");
        var matches = run.Approvals.Where(approval => approval.Request.CallId == resolution.CallId).ToArray();
        if (matches is not [var pending] || pending.Resolution is not null)
            throw new InvalidDataException("A child approval resolution has no pending request.");
        var resolved = pending with { Resolution = resolution };
        run.ValidateApproval(resolved);
        _ = Netclaw.Actors.Authorization.Consent.ConsentAnswerCodec.FromJournalText(resolution.Decision);
        return this with { ChildRuns = ChildRuns.SetItem(runId, run with
        { Approvals = run.Approvals.Select(approval => approval == pending ? resolved : approval).ToArray() }) };
    }

    public SessionState Apply(ChildRunEvent evt)
    {
        if (!ChildRuns.TryGetValue(evt.RunId, out var run) || run.StartKey.SessionId != evt.SessionId)
            throw new InvalidDataException("A child lifecycle fact has no accepted session owner.");
        var next = evt switch
        {
            ChildRunEvent.Started when run.StartedAtMs is null && run.Terminal is null && run.CancellationRequestedAtMs is null =>
                run with { StartedAtMs = evt.RecordedAtMs },
            ChildRunEvent.Checkpointed checkpoint when run.Terminal is null && run.DispatchClosedAtMs is null
                && checkpoint.Checkpoint.CompletedRound > (run.ChildCheckpoint?.CompletedRound ?? 0) =>
                run with { ChildCheckpoint = checkpoint.Checkpoint },
            ChildRunEvent.CancellationRequested when run.Terminal is null && run.CancellationRequestedAtMs is null =>
                run with { CancellationRequestedAtMs = evt.RecordedAtMs },
            ChildRunEvent.DispatchClosed when run.CancellationRequestedAtMs is not null && run.DispatchClosedAtMs is null && run.Terminal is null =>
                run with { DispatchClosedAtMs = evt.RecordedAtMs },
            ChildRunEvent.TerminalRecorded terminal when run.Terminal is null && terminal.TerminalSequenceNr > 0
                && !ChildRuns.Values.Any(other => other.TerminalSequenceNr == terminal.TerminalSequenceNr)
                && (run.CancellationRequestedAtMs is null || run.DispatchClosedAtMs is not null) =>
                run with { Terminal = terminal.Terminal, TerminalSequenceNr = terminal.TerminalSequenceNr },
            ChildRunEvent.ResultPrepared prepared when run.Terminal is not null && run.PreparedTerminal is null
                && prepared.TerminalSequenceNr == run.TerminalSequenceNr
                && SameTerminalOutcome(run.Terminal, prepared.Terminal) =>
                run with { PreparedTerminal = prepared.Terminal },
            ChildRunEvent.DeliveryAdmitted delivery when run.PreparedTerminal is not null && run.StartBatchSettled && run.DeliveryInputId is null =>
                ValidateChildDelivery(run, delivery.Input),
            _ => throw new InvalidDataException("A child lifecycle fact violates its committed order.")
        };
        next.Validate();
        var state = this with { ChildRuns = ChildRuns.SetItem(run.RunId, next) };
        return evt is ChildRunEvent.DeliveryAdmitted admitted ? state.AdmitInputCore(admitted.Input) : state;
    }

    private SessionState AdoptChildContinuation(ToolTaskAdopted evt)
    {
        if (evt.ContinuedJobKey is not null || evt.InputIds.Count == 0 || evt.InputIds.Distinct().Count() != evt.InputIds.Count)
            throw new InvalidDataException("A child continuation does not name the next canonical input prefix.");
        // Consumed inputs and compacted pairs can leave the transcript. Durable adoption remains authoritative.
        var alreadyAdopted = AdoptedTaskContext is { } && AdoptedTaskInputIds.SequenceEqual(evt.InputIds);
        if (alreadyAdopted)
        {
            if (evt.ContinuedChildRunId is not { } id || !ChildRuns.TryGetValue(id, out var duplicate)
                || duplicate.DeliveryInputId != evt.InputIds[^1] || duplicate.PreparedTerminal is null
                || !duplicate.StartBatchSettled || duplicate.StartKey.SessionId != evt.SessionId
                || !SameCanonicalContext(duplicate.OriginalContext, evt.TurnContext)
                || !SameCanonicalContext(AdoptedTaskContext!, evt.TurnContext))
                throw new InvalidDataException("A repeated child adoption differs from its canonical authority.");
            if (!PendingInputs.Take(evt.InputIds.Count).Select(static input => input.InputId).SequenceEqual(evt.InputIds))
            {
                ValidateConsumedChildPairs(evt);
                return this;
            }
        }
        if (!PendingInputs.Take(evt.InputIds.Count).Select(static input => input.InputId).SequenceEqual(evt.InputIds))
            throw new InvalidDataException("A child continuation does not name the next canonical input prefix.");
        var prefix = PendingInputs.Take(evt.InputIds.Count).ToArray();
        var run = GetChildContinuation(prefix[^1]);
        if (evt.ContinuedChildRunId != run.RunId || !SameCanonicalContext(evt.TurnContext, run.OriginalContext))
            throw new InvalidDataException("A child continuation differs from its original authority.");
        foreach (var input in prefix)
        {
            var sibling = GetChildContinuation(input);
            if (!SameCanonicalContext(sibling.OriginalContext, run.OriginalContext))
                throw new InvalidDataException("A child prefix merges distinct parent tasks.");
        }
        if (ParkedToolBatchHistory.FindRedrivableAssistantMessage(History, null) is not null)
            throw new InvalidDataException("A child continuation requires canonical closure of the prior tool batch.");
        var history = History;
        foreach (var input in prefix)
        {
            var sibling = GetChildContinuation(input);
            var callId = input.UserMessage.ToolCallId!.Value;
            var canonicalCall = ChildRunDelivery.Call(sibling, callId);
            var existing = history.Where(message => message.ToolCalls.Any(call => call.CallId == callId)).ToArray();
            if (existing.Length == 0)
            {
                if (history.Any(message => message.Role == ChatRole.Tool && message.ToolCallId == callId))
                    throw new InvalidDataException("A child continuation has an orphan result correlation.");
                history = history.Add(canonicalCall).Add(input.UserMessage);
                continue;
            }
            ValidateChildPair(history, canonicalCall, input.UserMessage);
        }
        if (alreadyAdopted)
            return this;
        var next = this with
        {
            History = history,
            AdoptedTaskContext = run.OriginalContext, AdoptedTaskInputIds = evt.InputIds,
            LoopAdmission = null, LoopObservations = []
        };
        return next;
    }

    private void ValidateConsumedChildPairs(ToolTaskAdopted evt)
    {
        var runs = ChildRuns.Values.Where(run => run.DeliveryInputId is { } input && evt.InputIds.Contains(input)).ToArray();
        if (runs.Length != evt.InputIds.Count)
            throw new InvalidDataException("A repeated child adoption lacks its canonical delivered runs.");
        foreach (var run in runs)
        {
            if (run.PreparedTerminal is null || !run.StartBatchSettled || run.StartKey.SessionId != evt.SessionId
                || !SameCanonicalContext(run.OriginalContext, evt.TurnContext))
                throw new InvalidDataException("A repeated child adoption differs from its canonical authority.");
            var body = ChildRunDelivery.Body(run);
            var toolName = ChildRunDelivery.ToolName(run);
            // Retained content can identify a pair. Its absence after compaction does not identify a new adoption.
            var ids = History.SelectMany(message => message.ToolCalls)
                .Where(call => call.Name.Value == toolName
                    && call.ArgumentsJson == ChildRunDelivery.Call(run, call.CallId).ToolCalls[0].ArgumentsJson)
                .Select(call => call.CallId)
                .Concat(History.Where(message => message.Role == ChatRole.Tool && message.Name == toolName
                    && message.Content == body && message.ToolCallId is not null).Select(message => message.ToolCallId!.Value))
                .Distinct();
            foreach (var id in ids)
                ValidateChildPair(History, ChildRunDelivery.Call(run, id), new SerializableChatMessage
                { Role = ChatRole.Tool, ToolCallId = id, Name = toolName, Content = body });
        }
    }

    private static void ValidateChildPair(IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage canonicalCall, SerializableChatMessage canonicalResult)
    {
        var callId = canonicalResult.ToolCallId!.Value;
        var existing = history.Where(message => message.ToolCalls.Any(call => call.CallId == callId)).ToArray();
        if (existing is not [var assistant] || assistant.Role != canonicalCall.Role
            || assistant.Content != canonicalCall.Content || assistant.Name != canonicalCall.Name
            || assistant.ToolCallId != canonicalCall.ToolCallId || assistant.MediaReferences.Count != 0
            || !assistant.ToolCalls.SequenceEqual(canonicalCall.ToolCalls))
            throw new InvalidDataException("A repeated child continuation differs from its canonical call.");
        var results = ParkedToolBatchHistory.FindToolResultsFor(history, assistant);
        if (results is not [var result] || result.Role != canonicalResult.Role
            || result.ToolCallId != callId || result.Name != canonicalResult.Name
            || result.Content != canonicalResult.Content || result.ToolCalls.Count != 0 || result.MediaReferences.Count != 0)
            throw new InvalidDataException("A repeated child continuation differs from its canonical result.");
    }

    private static bool SameTerminalOutcome(ChildRunTerminal original, ChildRunTerminal prepared)
        => original.Lost == prepared.Lost && original.Result.Output == prepared.Result.Output
           && original.Result.Outcome == prepared.Result.Outcome && original.Result.OutcomeReason == prepared.Result.OutcomeReason
           && original.Result.RunId == prepared.Result.RunId && original.Result.ScopeId == prepared.Result.ScopeId
           && original.Result.AgentName == prepared.Result.AgentName;

    private BackgroundChildRun ValidateChildDelivery(BackgroundChildRun run, InputAdmitted input)
    {
        ValidateChildDeliveryContent(run, input);
        var callId = input.UserMessage.ToolCallId!.Value;
        if (History.Any(message => message.ToolCalls.Any(call => call.CallId == callId))
            || PendingInputs.Any(pending => pending.InputId == input.InputId))
            throw new InvalidDataException("A child delivery repeats a committed input or provider correlation.");
        return run with { DeliveryInputId = input.InputId };
    }

    private static void ValidateChildDeliveryContent(BackgroundChildRun run, InputAdmitted input)
    {
        if (input.SessionId != run.StartKey.SessionId || input.SourceChildRunId != run.RunId
            || input.SourceBackgroundJobId is not null || input.BackgroundJobLineageVersion != 0 || input.BackgroundJobOrigin is not null
            || !SameCanonicalContext(input.TurnContext, run.OriginalContext)
            || input.InputId.Value != $"child-result-{run.RunId.Value}"
            || input.UserMessage.Role != ChatRole.Tool || input.UserMessage.Name != ChildRunDelivery.ToolName(run)
            || input.UserMessage.Content != ChildRunDelivery.Body(run) || input.UserMessage.ToolCallId is not { } callId
            || string.IsNullOrWhiteSpace(callId.Value)
            || run.StartKey is ChildRunStartKey.Tool tool && callId == tool.CallId
            || input.UserMessage.ToolCalls.Count != 0 || input.UserMessage.MediaReferences.Count != 0
            || input.ExecutableText is not null)
            throw new InvalidDataException("A child delivery differs from its canonical result, origin, or authority.");
    }

    internal BackgroundChildRun GetChildContinuation(InputAdmitted input)
    {
        if (input.SourceBackgroundJobId is not null || input.SourceChildRunId is not { } id
            || !ChildRuns.TryGetValue(id, out var run) || run.DeliveryInputId != input.InputId
            || run.PreparedTerminal is null || !run.StartBatchSettled
            || run.StartKey.SessionId != input.SessionId || !SameCanonicalContext(run.OriginalContext, input.TurnContext))
            throw new InvalidDataException("A child continuation lacks canonical committed delivery evidence.");
        ValidateChildDeliveryContent(run, input);
        return run;
    }
}
