// -----------------------------------------------------------------------
// <copyright file="LegacyCoordinationReplay.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Text.Json;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Proto = Netclaw.Actors.Serialization.Proto;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>Applies deployed coordination facts during recovery only.</summary>
public static class LegacyCoordinationReplay
{
    public static SessionState Apply(SessionState state, LegacyCoordinationEvent evt)
    {
        return evt.LegacyPayload switch
        {
            Proto.ToolTaskAdoptedProto adoption => ApplyAdoption(state, evt, adoption),
            Proto.ChildRunAcceptedProto accepted => ApplyAccepted(state, evt, accepted),
            Proto.ChildRunEventProto childEvent => ApplyChildEvent(state, evt, childEvent),
            Proto.InputAdmittedProto input => ApplyExtendedInput(state, evt, input),
            Proto.InputClosedProto input => ValidateLegacyInputClosed(state, evt, input),
            Proto.ToolBatchStartedProto batch => ValidateLegacyToolBatch(state, evt, batch),
            _ => throw new InvalidDataException($"Unsupported deployed coordination manifest '{evt.Manifest}'.")
        };
    }

    public static ISessionEvent? GetCoreEvent(SessionState state, LegacyCoordinationEvent evt)
        => evt.LegacyPayload switch
        {
            Proto.InputClosedProto input => NetclawProtoMapper.FromProto(input),
            Proto.ToolBatchStartedProto { MetadataOnly: false } batch => NetclawProtoMapper.FromProto(batch),
            Proto.ToolBatchStartedProto => null,
            _ => throw new InvalidDataException($"Legacy manifest '{evt.Manifest}' has no ordinary core event.")
        };

    public static ToolBatchStarted TranslateParentDecision(SessionState state, ToolBatchStarted evt)
    {
        var translated = TranslateParentInputs(state, evt.ConsumedInputIds, evt.ChildContinuation);
        return evt with
        {
            ConsumedInputIds = translated.InputIds,
            ChildContinuation = translated.Continuation,
            UserMessage = translated.LastOrdinaryMessage ?? evt.UserMessage
        };
    }

    public static TurnRecorded TranslateParentDecision(SessionState state, TurnRecorded evt)
    {
        var translated = TranslateParentInputs(state, evt.ConsumedInputIds, evt.ChildContinuation);
        return evt with
        {
            ConsumedInputIds = translated.InputIds,
            ChildContinuation = translated.Continuation,
            UserMessage = translated.LastOrdinaryMessage ?? evt.UserMessage
        };
    }

    public static InputClosed TranslateParentDecision(SessionState state, InputClosed evt)
    {
        var translated = TranslateParentInputs(state, evt.InputIds, evt.ChildContinuation);
        return evt with { InputIds = translated.InputIds, ChildContinuation = translated.Continuation };
    }

    private static (IReadOnlyList<InputId> InputIds, ChildContinuation? Continuation,
        SerializableChatMessage? LastOrdinaryMessage) TranslateParentInputs(
        SessionState state, IReadOnlyList<InputId> inputIds, ChildContinuation? current)
    {
        if (current is not null)
            return (inputIds, current, null);
        if (inputIds.Distinct().Count() != inputIds.Count)
            throw new InvalidDataException("A parent decision repeats an input ID.");
        var correlations = state.LegacyCoordinationEvidence.Where(item =>
            item.Disposition == LegacyCoordinationDisposition.AppliedChildContinuation &&
            item.ChildRunId is { } runId && !state.TryGetLegacyChildDisposition(runId, out _) &&
            state.ChildRuns.TryGetValue(runId, out var run) && IsLegacyDelivered(run) &&
            (inputIds.Count == 0 || item.InputIds.Any(inputIds.Contains))).ToArray();
        if (correlations.Length == 0)
            return (inputIds, null, null);
        if (correlations.Length != 1)
            throw new InvalidDataException("A parent decision has more than one active legacy child correlation.");
        var correlation = correlations[0];
        if (correlation.ChildRunId is not { } childId || correlation.DeliveryCallId is not { } callId ||
            correlation.InputIds.Count != 1 ||
            state.ChildRuns[childId].Lifecycle is not ChildRunLifecycle.Reported
                { Report: ChildRunReport.LegacyDelivery delivered } ||
            delivered.DeliveryCallId != callId)
            throw new InvalidDataException("A parent decision has no exact active child delivery.");
        var ordinaryIds = inputIds.Where(id => !correlation.InputIds.Contains(id)).ToArray();
        var pending = state.PendingInputs.ToDictionary(input => input.InputId);
        if (ordinaryIds.Any(id => !pending.ContainsKey(id)))
            throw new InvalidDataException("A parent decision names an ordinary input that is not pending.");
        var lastOrdinary = ordinaryIds.Length > 0 && inputIds[^1] == correlation.InputIds[0]
            ? pending[ordinaryIds[^1]].UserMessage : null;
        return (ordinaryIds, new ChildContinuation(childId, callId), lastOrdinary);
    }

    // A deployed adoption put the tool pair into the history, and the parent decision that closes it is still to come.
    private static bool IsLegacyDelivered(BackgroundChildRun run)
        => run.Lifecycle is ChildRunLifecycle.Reported { Report: ChildRunReport.LegacyDelivery };

    public static TurnContextRecord? GetLegacyTaskContext(LegacyCoordinationEvent evt)
    {
        if (evt.LegacyPayload is not Proto.ToolBatchStartedProto { MetadataOnly: true, LegacyTaskContext: not null } batch)
            return null;
        return NetclawProtoMapper.FromLegacyTurnContext(batch.LegacyTaskContext);
    }

    public static ImmutableArray<ISessionEvent> GetLegacyChildApprovalEvents(SessionState state)
        => [.. state.LegacyCoordinationEvidence
            .SelectMany(evidence => evidence.ApprovalEvents)
            .Where(evt => evt is ToolApprovalRequested or ToolApprovalResolved)];

    private static SessionState ApplyAdoption(SessionState state, LegacyCoordinationEvent evt,
        Proto.ToolTaskAdoptedProto adoption)
    {
        var inputIds = adoption.InputIds.Select(static id => new InputId(id)).ToArray();
        var context = adoption.TurnContext is null
            ? null
            : NetclawProtoMapper.FromLegacyTurnContext(adoption.TurnContext);
        var identityReason = ValidateAdoptionIdentity(evt.SessionId, inputIds, context);
        if (identityReason is null && adoption.HasContinuedChildRunId)
            return ApplyChildAdoption(state, evt, adoption, inputIds, context!);

        var reason = identityReason ?? ValidateOrdinaryAdoption(state, inputIds, adoption);

        if (reason is null)
        {
            var differsFromAdmission = inputIds.Any(id =>
            {
                var input = state.PendingInputs.First(item => item.InputId == id);
                return !SameContext(context!, input.TurnContext);
            });
            return state.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
            {
                Manifest = evt.Manifest,
                Payload = differsFromAdmission ? evt.Payload : [],
                Disposition = LegacyCoordinationDisposition.AppliedOrdinaryAdoption,
                InputIds = inputIds,
                ActiveInputIds = inputIds,
                AdoptedContext = context
            });
        }

        return Quarantine(state, evt, inputIds, reason);
    }

    private static string? ValidateAdoptionIdentity(SessionId sessionId,
        IReadOnlyList<InputId> inputIds, TurnContextRecord? context)
    {
        if (context is null)
            return "The deployed adoption has no authority context.";
        if (inputIds.Count == 0 || inputIds.Any(static id => string.IsNullOrWhiteSpace(id.Value))
            || inputIds.Distinct().Count() != inputIds.Count)
            return "The deployed adoption has empty or repeated input IDs.";
        if (context.SessionId != sessionId)
            return "The deployed adoption context names a different session.";
        return null;
    }

    private static string? ValidateOrdinaryAdoption(SessionState state, IReadOnlyList<InputId> inputIds,
        Proto.ToolTaskAdoptedProto adoption)
    {
        if (adoption.HasContinuedChildRunId || adoption.StartsChildContinuationWindow)
            return "The deployed ordinary adoption has child-continuation identity without a correlated child delivery.";
        if (state.PendingInputs.Take(inputIds.Count).Select(static input => input.InputId).SequenceEqual(inputIds) is false)
            return "The deployed adoption IDs do not match the next pending input prefix.";
        if (inputIds.Any(id => state.PendingInputs.All(input => input.InputId != id)))
            return "The deployed adoption names an input that is not pending.";
        if (inputIds.Any(state.IsLegacyInputQuarantined))
            return "The deployed adoption names a quarantined legacy input.";
        var selected = state.PendingInputs.Take(inputIds.Count).ToArray();
        var adoptedContext = adoption.TurnContext is null ? null : NetclawProtoMapper.FromLegacyTurnContext(adoption.TurnContext);
        if (adoptedContext is null || !SameContext(adoptedContext, selected[^1].TurnContext)
            || selected.Any(input => !SameAuthority(input.TurnContext, adoptedContext)))
            return "The deployed adoption differs from its pending input authority.";
        if (selected[^1].SourceBackgroundJobId is { } jobId)
        {
            if (selected.Length != 1 || adoption.ContinuedJobKey != jobId.Value)
                return "The deployed job adoption does not match the exact pending job input.";
            if (!IsValidLegacyJobInput(state, selected[0], out var reason))
                return $"The deployed job adoption has no valid exact lineage: {reason}.";
        }
        else if (adoption.HasContinuedJobKey)
        {
            return "The deployed adoption names a job that its exact pending inputs do not contain.";
        }
        return null;
    }

    private static bool IsValidLegacyJobInput(SessionState state, InputAdmitted input, out string reason)
    {
        reason = "invalid trusted job metadata";
        if (input.SessionId != input.TurnContext.SessionId
            || input.SourceBackgroundJobId is not { } sourceJob || input.SourceMessageId != sourceJob.Value
            || input.TurnContext.TurnId != sourceJob.Value
            || input.TurnContext.SourceKind != Netclaw.Actors.Jobs.BackgroundJobManagerActor.SourceKind
            || input.TurnContext.RequesterPrincipal != Netclaw.Configuration.PrincipalClassification.VerifiedAutomation
            || input.TurnContext.TransportAuthenticity != Netclaw.Configuration.TransportAuthenticity.LocalProcess
            || !sourceJob.Value.StartsWith(Netclaw.Actors.Jobs.BackgroundJobManagerActor.JobDeliveryKeyPrefix,
                StringComparison.Ordinal))
            return false;

        if (state.ActiveBackgroundJobs.TryGetValue(sourceJob.Value, out var tracked)
            && (tracked.JobId.Value != sourceJob.Value[Netclaw.Actors.Jobs.BackgroundJobManagerActor.JobDeliveryKeyPrefix.Length..]
                || tracked.Audience != input.TurnContext.Audience || tracked.Boundary != input.TurnContext.Boundary))
        {
            reason = "the trusted source differs from the tracked job owner";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static SessionState ApplyChildAdoption(SessionState state, LegacyCoordinationEvent evt,
        Proto.ToolTaskAdoptedProto adoption, IReadOnlyList<InputId> inputIds, TurnContextRecord context)
    {
        if (!adoption.StartsChildContinuationWindow || adoption.HasContinuedJobKey)
            return Quarantine(state, evt, inputIds, "The deployed child adoption has invalid continuation-window or job identity.");
        var orderedCorrelations = state.LegacyCoordinationEvidence
            .Where(item => item.Disposition == LegacyCoordinationDisposition.AppliedChildContinuation
                           && item.InputIds.Count == 1 && inputIds.Contains(item.InputIds[0]))
            .ToArray();
        var correlations = orderedCorrelations.ToDictionary(static item => item.InputIds[0]);
        if (!orderedCorrelations.Select(static item => item.InputIds[0]).SequenceEqual(inputIds)
            || correlations.Count != inputIds.Count
            || !string.Equals(adoption.ContinuedChildRunId, correlations[inputIds[^1]].ChildRunId?.Value,
                StringComparison.Ordinal))
            return Quarantine(state, evt, inputIds, "The deployed child adoption lacks exact delivery correlation.");

        if (correlations.Count != 1)
        {
            var quarantined = Quarantine(state, evt, inputIds,
                "The deployed adoption contains multiple child deliveries, which the current parent path cannot restore safely.");
            foreach (var evidence in correlations.Values)
            {
                if (evidence.ChildRunId is not { } runId)
                    continue;
                quarantined = quarantined.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
                {
                    Manifest = evt.Manifest,
                    Payload = [],
                    Disposition = LegacyCoordinationDisposition.UnresolvedLegacyChild,
                    InputIds = evidence.InputIds,
                    ActiveInputIds = evidence.InputIds,
                    AdoptedContext = evidence.AdoptedContext,
                    ChildRunId = runId,
                    DeliveryCallId = evidence.DeliveryCallId,
                    Reason = "The deployed parent adoption has multiple child deliveries."
                });
            }
            return quarantined;
        }

        var runs = new List<(SubAgentRunId Id, ChildRunTerminal Terminal, LegacyPreparedResult Delivery)>();
        foreach (var inputId in inputIds)
        {
            var evidence = correlations[inputId];
            if (evidence.ChildRunId is not { } runId || evidence.DeliveryCallId is not { } callId
                || !state.ChildRuns.TryGetValue(runId, out var run)
                || run.Lifecycle is not ChildRunLifecycle.Terminal { LegacyPrepared: { } prepared } terminal
                || prepared.Result.ToolCallId != callId || !SameContext(context, run.OriginalContext))
                return Quarantine(state, evt, inputIds, "The deployed child adoption differs from its prepared result or authority.");
            runs.Add((runId, terminal.Result, prepared));
        }

        var history = state.History;
        var childRuns = state.ChildRuns;
        foreach (var (runId, terminal, delivery) in runs)
        {
            if (!HasExactPair(history, delivery))
            {
                if (history.Any(message => message.ToolCalls.Any(call => call.CallId == delivery.Result.ToolCallId)
                                           || message.ToolCallId == delivery.Result.ToolCallId))
                    return Quarantine(state, evt, inputIds, "The deployed child delivery call ID conflicts with session history.");
                history = history.Add(delivery.Call).Add(delivery.Result);
            }
            childRuns = childRuns.SetItem(runId, childRuns[runId] with
            {
                Lifecycle = new ChildRunLifecycle.Reported(terminal.Result.Outcome, terminal.Result.OutcomeReason,
                    new ChildRunReport.LegacyDelivery(delivery.Result.ToolCallId!.Value))
            });
        }

        var correlation = correlations[inputIds[0]];
        return state with
        {
            History = history,
            ChildRuns = childRuns,
            LegacyCoordinationEvidence = state.LegacyCoordinationEvidence
                .Where(item => !ReferenceEquals(item, correlation))
                .Append(correlation with { ActiveInputIds = inputIds.ToArray() })
                .ToImmutableList()
        };
    }

    private static SessionState ApplyAccepted(SessionState state, LegacyCoordinationEvent evt,
        Proto.ChildRunAcceptedProto accepted)
    {
        var legacy = accepted.Run ?? throw new InvalidDataException("Legacy child acceptance has no run record.");
        if (legacy.OriginalContext is null || legacy.InitialWorkingContext is null || legacy.InitialGit is null)
            throw new InvalidDataException("Legacy child acceptance lacks its original authority or working snapshot.");

        var session = new SessionId(legacy.OwnerSessionId);
        var turn = new TurnId(legacy.OriginalTurnId);
        ChildRunStartKey startKey = legacy.ActivationCase switch
        {
            Proto.BackgroundChildRunProto.ActivationOneofCase.ToolCallId =>
                new ChildRunStartKey.Tool(session, turn.Value, new ToolCallId(legacy.ToolCallId)),
            Proto.BackgroundChildRunProto.ActivationOneofCase.SlashInputId =>
                new ChildRunStartKey.Slash(session, turn.Value, new InputId(legacy.SlashInputId)),
            _ => throw new InvalidDataException("Legacy child acceptance has no activation identity.")
        };

        if (legacy.StartedAtMs != 0 || legacy.HasCancellationRequestedAtMs || legacy.Terminal is not null
            || legacy.PreparedTerminal is not null || legacy.HasDeliveryInputId)
            throw new InvalidDataException("Legacy child acceptance contains lifecycle facts outside its accepted event.");

        if (legacy.StartBatchSettled != (startKey is ChildRunStartKey.Slash))
            throw new InvalidDataException("Legacy child acceptance has inconsistent activation settlement.");

        var run = new BackgroundChildRun
        {
            RunId = new SubAgentRunId(legacy.RunId),
            ScopeId = new SubAgentScopeId(legacy.ScopeId),
            StartKey = startKey,
            AgentName = new AgentName(legacy.AgentName),
            SourceOperation = legacy.SourceOperation,
            ArgumentsDigest = legacy.ArgumentsDigest,
            OriginalContext = NetclawProtoMapper.FromLegacyTurnContext(legacy.OriginalContext),
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = NetclawProtoMapper.FromProto(legacy.InitialWorkingContext),
                Git = FromLegacyGitInspection(legacy.InitialGit)
            },
            AcceptedAtMs = legacy.AcceptedAtMs,
            Lifecycle = new ChildRunLifecycle.Accepted(null)
        };
        if (evt.SessionId != session || run.OriginalContext.SessionId != session
            || run.OriginalContext.TurnId != legacy.OriginalTurnId || state.ChildRuns.ContainsKey(run.RunId)
            || state.ChildRuns.Values.Any(existing => existing.StartKey == startKey))
            throw new InvalidDataException("Legacy child acceptance repeats or changes its owner identity.");
        if (startKey is ChildRunStartKey.Tool tool
            && !state.History.Any(message => message.ToolCalls.Any(call =>
                call.CallId == tool.CallId && call.Name.Value == run.SourceOperation)))
            throw new InvalidDataException("Legacy child acceptance has no matching parent tool call.");
        return state with { ChildRuns = state.ChildRuns.Add(run.RunId, run) };
    }

    private static SessionState ApplyChildEvent(SessionState state, LegacyCoordinationEvent evt,
        Proto.ChildRunEventProto childEvent)
    {
        var runId = new SubAgentRunId(childEvent.RunId);
        if (!state.ChildRuns.TryGetValue(runId, out var run))
            throw new InvalidDataException("A deployed child lifecycle event has no accepted run.");

        switch (childEvent.FactCase)
        {
            case Proto.ChildRunEventProto.FactOneofCase.Started when childEvent.Started:
                return state;
            case Proto.ChildRunEventProto.FactOneofCase.Checkpoint:
                return state.Apply(new ChildRunEvent.Checkpointed(FromLegacyCheckpoint(childEvent.Checkpoint))
                {
                    SessionId = evt.SessionId, RunId = runId, RecordedAtMs = childEvent.RecordedAtMs
                });
            case Proto.ChildRunEventProto.FactOneofCase.CancellationRequested when childEvent.CancellationRequested:
                return state.Apply(new ChildRunEvent.CancellationRequested
                {
                    SessionId = evt.SessionId, RunId = runId, RecordedAtMs = childEvent.RecordedAtMs
                });
            case Proto.ChildRunEventProto.FactOneofCase.DispatchClosed when childEvent.DispatchClosed:
                if (run.Lifecycle is not ChildRunLifecycle.Cancelling)
                    throw new InvalidDataException("Legacy dispatch closure has no prior cancellation intent.");
                return state.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
                {
                    Manifest = evt.Manifest,
                    Payload = [],
                    Disposition = LegacyCoordinationDisposition.PreservedLegacyFacts,
                    ChildRunId = runId,
                    LegacyDispatchClosed = true
                });
            case Proto.ChildRunEventProto.FactOneofCase.Terminal:
                if (childEvent.TerminalSequenceNr <= 0)
                    throw new InvalidDataException("A deployed child terminal has no positive journal sequence.");
                if (run.Lifecycle is ChildRunLifecycle.Cancelling
                    && !state.LegacyCoordinationEvidence.Any(item => item.ChildRunId == runId && item.LegacyDispatchClosed))
                    throw new InvalidDataException("Legacy child cancellation has no durable dispatch-closure fact.");
                var checkpoint = run.Lifecycle switch
                {
                    ChildRunLifecycle.Accepted accepted => accepted.Checkpoint,
                    ChildRunLifecycle.Cancelling cancelling => cancelling.Checkpoint,
                    _ => null
                };
                var terminalState = state.Apply(new ChildRunEvent.TerminalRecorded(FromLegacyTerminal(childEvent.Terminal))
                {
                    SessionId = evt.SessionId, RunId = runId, RecordedAtMs = childEvent.RecordedAtMs
                });
                return terminalState.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
                {
                    Manifest = evt.Manifest,
                    Payload = [],
                    Disposition = LegacyCoordinationDisposition.PreservedLegacyFacts,
                    ChildRunId = runId,
                    LegacyCheckpoint = checkpoint,
                    LegacyTerminalSequenceNr = childEvent.TerminalSequenceNr
                });
            case Proto.ChildRunEventProto.FactOneofCase.Prepared:
            {
                var terminalEvidence = state.LegacyCoordinationEvidence.LastOrDefault(item =>
                    item.ChildRunId == runId && item.LegacyTerminalSequenceNr is not null);
                if (childEvent.TerminalSequenceNr <= 0 || terminalEvidence?.LegacyTerminalSequenceNr != childEvent.TerminalSequenceNr
                    || run.Lifecycle is not ChildRunLifecycle.Terminal terminal)
                    throw new InvalidDataException("Legacy prepared child result has no terminal state.");
                var preparedTerminal = FromLegacyTerminal(childEvent.Prepared);
                if (!SameTerminalOutcome(terminal.Result, preparedTerminal))
                    throw new InvalidDataException("Legacy child enrichment changes its terminal outcome.");
                return state with
                {
                    ChildRuns = state.ChildRuns.SetItem(runId, run with
                    {
                        Lifecycle = new ChildRunLifecycle.Terminal(preparedTerminal)
                    })
                };
            }
            case Proto.ChildRunEventProto.FactOneofCase.Delivery:
                return ApplyLegacyDelivery(state, evt, run, childEvent.Delivery);
            default:
                throw new InvalidDataException("A deployed child lifecycle event has an invalid fact.");
        }
    }

    private static SessionState ApplyLegacyDelivery(SessionState state, LegacyCoordinationEvent evt,
        BackgroundChildRun run, Proto.InputAdmittedProto proto)
    {
        if (run.Lifecycle is not ChildRunLifecycle.Terminal { LegacyPrepared: null } terminal || proto.UserMessage is null
            || proto.HasSourceChildRunId is false || proto.SourceChildRunId != run.RunId.Value)
            throw new InvalidDataException("Legacy child delivery has no matching terminal run.");
        var input = NetclawProtoMapper.FromProto(proto);
        var toolName = LegacyToolName(run);
        var legacyFacts = state.LegacyCoordinationEvidence.LastOrDefault(item => item.ChildRunId == run.RunId);
        var expectedBody = LegacyTerminalBody(run, terminal.Result, legacyFacts?.LegacyCheckpoint);
        if (input.SessionId != run.StartKey.SessionId || !SameContext(input.TurnContext, run.OriginalContext)
            || input.UserMessage.Role != ChatRole.Tool || input.UserMessage.Name != toolName
            || input.UserMessage.Content != expectedBody || input.UserMessage.ToolCallId is not { } callId
            || run.StartKey is ChildRunStartKey.Tool original && original.CallId == callId)
            throw new InvalidDataException("Legacy child delivery differs from its exact source operation or result.");

        var call = LegacyDeliveryCall(run, callId);
        // Deployed terminal deltas do not prove that the current review gate accepted them.
        var delivery = new LegacyPreparedResult(call, input.UserMessage, null);
        if (state.History.Any(message => message.ToolCalls.Any(call => call.CallId == callId)
                                        || message.Role == ChatRole.Tool && message.ToolCallId == callId))
            throw new InvalidDataException("Legacy child delivery repeats a history call ID.");
        var evidence = new LegacyCoordinationEvidence
        {
            Manifest = evt.Manifest,
            Payload = [],
            Disposition = LegacyCoordinationDisposition.AppliedChildContinuation,
            InputIds = [input.InputId],
            ActiveInputIds = [input.InputId],
            AdoptedContext = run.OriginalContext,
            ChildRunId = run.RunId,
            DeliveryCallId = callId
        };
        return state with
        {
            ChildRuns = state.ChildRuns.SetItem(run.RunId, run with
            {
                InitialWorkingSnapshot = null,
                Lifecycle = terminal with { LegacyPrepared = delivery }
            }),
            LegacyCoordinationEvidence = state.LegacyCoordinationEvidence
                .Where(item => item.ChildRunId != run.RunId)
                .Append(evidence)
                .ToImmutableList()
        };
    }

    private static SessionState ApplyExtendedInput(SessionState state, LegacyCoordinationEvent evt,
        Proto.InputAdmittedProto proto)
    {
        var input = NetclawProtoMapper.FromProto(proto);
        if (input.SessionId != evt.SessionId || string.IsNullOrWhiteSpace(input.InputId.Value)
            || state.PendingInputs.Any(existing => existing.InputId == input.InputId))
            throw new InvalidDataException("A deployed input admission repeats or changes its session identity.");
        var next = state.Apply(input);
        var inputIds = new[] { input.InputId };
        if (proto.BackgroundJobLineageVersion != 0 || proto.BackgroundJobOrigin is not null)
            return Quarantine(next, evt, inputIds,
                "The deployed input has versioned job lineage that the current runtime cannot validate against its producer record.");
        if (proto.HasSourceBackgroundJobId && (input.SourceBackgroundJobId is not { } jobId
                || string.IsNullOrWhiteSpace(jobId.Value) || input.SourceMessageId != jobId.Value
                || input.TurnContext.SourceKind != Netclaw.Actors.Jobs.BackgroundJobManagerActor.SourceKind
                || input.TurnContext.RequesterPrincipal != Netclaw.Configuration.PrincipalClassification.VerifiedAutomation
                || input.TurnContext.TransportAuthenticity != Netclaw.Configuration.TransportAuthenticity.LocalProcess))
            return Quarantine(next, evt, inputIds,
                "The deployed job input does not match its trusted source identity.");
        if (proto.HasSourceChildRunId)
        {
            var runId = new SubAgentRunId(proto.SourceChildRunId);
            var unresolved = next.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
            {
                Manifest = evt.Manifest,
                Payload = evt.Payload,
                Disposition = LegacyCoordinationDisposition.QuarantinedInvalidAdoption,
                InputIds = inputIds,
                ActiveInputIds = inputIds,
                AdoptedContext = input.TurnContext,
                ChildRunId = runId,
                Reason = "A standalone deployed child input has no exact lifecycle delivery record."
            });
            if (next.ChildRuns.ContainsKey(runId))
                unresolved = unresolved.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
                {
                    Manifest = evt.Manifest,
                    Payload = [],
                    Disposition = LegacyCoordinationDisposition.UnresolvedLegacyChild,
                    InputIds = inputIds,
                    ActiveInputIds = inputIds,
                    AdoptedContext = input.TurnContext,
                    ChildRunId = runId,
                    Reason = "A standalone deployed child input has no exact lifecycle delivery record."
                });
            return unresolved;
        }
        if (!proto.HasSourceBackgroundJobId)
            return next;
        return next.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
        {
            Manifest = evt.Manifest,
            Payload = evt.Payload,
            Disposition = LegacyCoordinationDisposition.PreservedLegacyFacts,
            InputIds = inputIds,
            ActiveInputIds = inputIds,
            Reason = "The deployed input has a trusted legacy job source with no versioned producer lineage."
        });
    }

    private static SessionState ValidateLegacyInputClosed(SessionState state, LegacyCoordinationEvent evt,
        Proto.InputClosedProto proto)
    {
        var ids = proto.InputIds.Select(static value => new InputId(value)).ToArray();
        if (evt.SessionId != new SessionId(proto.SessionId?.Value ?? string.Empty)
            || ids.Any(static id => string.IsNullOrWhiteSpace(id.Value))
            || ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("A deployed input closure has invalid session or input identity.");

        var translated = TranslateParentDecision(state, NetclawProtoMapper.FromProto(proto));
        var pending = state.GetExecutablePendingInputs().Where(input => translated.InputIds.Contains(input.InputId)).ToArray();
        if (pending.Length != translated.InputIds.Count || !pending.Select(static input => input.InputId).SequenceEqual(translated.InputIds)
            || pending.Any(input => input.SessionId != evt.SessionId))
            throw new InvalidDataException("A deployed input closure does not match the exact pending input sequence.");

        var taskContext = translated.ChildContinuation is { } child
            ? state.ChildRuns[child.RunId].OriginalContext
            : pending.LastOrDefault()?.TurnContext;
        if (proto.HasTaskId && taskContext is not null && taskContext.TurnId != proto.TaskId)
            throw new InvalidDataException("A deployed input closure names a different adopted task authority.");
        if (taskContext is not null && pending.Any(input => !SameAuthority(input.TurnContext, taskContext)))
            throw new InvalidDataException("A deployed input closure mixes different input authority.");

        if (proto.HasSourceBackgroundJobId)
        {
            var jobId = new BackgroundJobId(proto.SourceBackgroundJobId);
            if (string.IsNullOrWhiteSpace(jobId.Value) || pending.Length != 1 || pending[0].SourceBackgroundJobId != jobId
                || pending[0].SourceMessageId != jobId.Value
                || pending[0].TurnContext.SourceKind != Netclaw.Actors.Jobs.BackgroundJobManagerActor.SourceKind
                || pending[0].TurnContext.RequesterPrincipal != Netclaw.Configuration.PrincipalClassification.VerifiedAutomation
                || pending[0].TurnContext.TransportAuthenticity != Netclaw.Configuration.TransportAuthenticity.LocalProcess)
                throw new InvalidDataException("A deployed job closure does not match its exact trusted pending input.");
        }
        else if (pending.Any(static input => input.SourceBackgroundJobId is not null))
        {
            throw new InvalidDataException("A deployed closure omits the pending input's background job identity.");
        }

        if (proto.RejectedJobReport is not null)
        {
            var report = NetclawProtoMapper.FromProto(proto.RejectedJobReport);
            if (!proto.HasSourceBackgroundJobId || report.Role != ChatRole.Assistant
                || report.ToolCalls.Count != 0 || report.MediaReferences.Count != 0
                || report.ToolCallId is not null || report.Name is not null)
                throw new InvalidDataException("A deployed rejected-job report has an unsafe role or shape.");
        }

        return state;
    }

    private static SessionState ValidateLegacyToolBatch(SessionState state, LegacyCoordinationEvent evt,
        Proto.ToolBatchStartedProto proto)
    {
        if (!proto.MetadataOnly && proto.LegacyTaskContext is null)
            return state;
        if (proto.LegacyTaskContext is null)
            throw new InvalidDataException("A deployed legacy tool batch has no authority context.");
        var context = NetclawProtoMapper.FromLegacyTurnContext(proto.LegacyTaskContext);
        if (context.SessionId != evt.SessionId
            || proto.LoopAdmission is not null && context.TurnId != proto.LoopAdmission.TaskId
            || proto.MetadataOnly && (proto.LoopAdmission is null || string.IsNullOrWhiteSpace(proto.LoopAdmission.TaskId)))
            throw new InvalidDataException("A deployed legacy tool batch changes its session or task authority.");
        var adopted = state.LegacyCoordinationEvidence.LastOrDefault(item =>
            item.Disposition == LegacyCoordinationDisposition.AppliedOrdinaryAdoption
            && item.AdoptedContext is not null && item.AdoptedContext.TurnId == context.TurnId);
        var pending = state.GetExecutablePendingInputs().Where(input => input.TurnContext.TurnId == context.TurnId).ToArray();
        if ((adopted?.AdoptedContext is { } adoptedContext && !SameContext(context, adoptedContext))
            || (pending.Length > 0 && pending.Any(input => !SameContext(context, input.TurnContext))))
            throw new InvalidDataException("A deployed metadata-only tool batch differs from its admitted authority.");
        return state;
    }

    private static SessionState Quarantine(SessionState state, LegacyCoordinationEvent evt,
        IReadOnlyList<InputId> inputIds, string reason)
        => state.RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
        {
            Manifest = evt.Manifest,
            Payload = evt.Payload,
            Disposition = LegacyCoordinationDisposition.QuarantinedInvalidAdoption,
            InputIds = inputIds,
            Reason = reason
        });

    internal static ChildRunCheckpoint FromLegacyCheckpoint(Proto.ChildRunCheckpointProto proto)
        => new(proto.CompletedRound, proto.Summary, FromLegacyWorkingDelta(proto.ConfirmedActivity
            ?? throw new InvalidDataException("A deployed child checkpoint has no activity record.")));

    internal static WorkingContextDelta FromLegacyWorkingDelta(Proto.ChildWorkingContextProto proto) => new()
    {
        ProjectDirectory = proto.HasProjectDirectory ? proto.ProjectDirectory : null,
        Worktree = proto.HasWorktree ? proto.Worktree : null,
        Branch = proto.HasBranch ? proto.Branch : null,
        Head = proto.HasHead ? proto.Head : null,
        ReadFiles = proto.ReadFiles.ToArray(),
        ConfirmedChangedFiles = proto.ConfirmedChangedFiles.ToArray(),
        ObservedChangedFiles = proto.ObservedChangedFiles.ToArray()
    };

    internal static ChildRunTerminal FromLegacyTerminal(Proto.ChildRunTerminalProto proto)
    {
        var outcome = CheckedEnum<SubAgentRunOutcome>(proto.Outcome);
        var completion = ChildRunCompletion.FromReportedOutcome(outcome,
            proto.HasOutcomeReason ? new SubAgentOutcomeReason(proto.OutcomeReason) : null,
            proto.WorkingContext is null ? null : FromLegacyWorkingDelta(proto.WorkingContext));
        var result = new SubAgentResult
        {
            Completion = completion,
            Output = proto.Output,
            AgentName = new AgentName(proto.AgentName),
            RunId = new SubAgentRunId(proto.RunId),
            ScopeId = new SubAgentScopeId(proto.ScopeId),
            FindingsCount = proto.FindingsCount,
            LogPath = proto.HasLogPath ? proto.LogPath : null,
            ArtifactDirectory = proto.HasArtifactDirectory ? proto.ArtifactDirectory : null,
            Findings = proto.Findings.Select(static item => new SubAgentFinding
            {
                Shape = CheckedEnum<SubAgentFindingShape>(item.Shape),
                Title = item.Title,
                Content = item.Content,
                Kind = item.Kind,
                Sensitivity = CheckedEnum<SubAgentFindingSensitivity>(item.Sensitivity),
                RecallMode = CheckedEnum<SubAgentFindingRecallMode>(item.RecallMode),
                UpdateSemantics = item.UpdateSemantics,
                Confidence = item.Confidence,
                Durability = CheckedEnum<SubAgentFindingDurability>(item.Durability),
                Reusability = CheckedEnum<SubAgentFindingReusability>(item.Reusability),
                Evidence = item.Evidence.ToArray(),
                FreshnessAtMs = item.HasFreshnessAtMs ? item.FreshnessAtMs : null
            }).ToList()
        };
        return new ChildRunTerminal(result, proto.Lost, proto.HasEvidenceWarning ? proto.EvidenceWarning : null);
    }

    internal static GitWorkingContextInspection FromLegacyGitInspection(Proto.GitWorkingContextInspectionProto proto)
    {
        if (proto.Kind != Proto.GitWorkingContextInspectionProto.Types.Kind.Available)
            return proto.Kind switch
            {
                Proto.GitWorkingContextInspectionProto.Types.Kind.Skipped => new GitWorkingContextInspection.Skipped(),
                Proto.GitWorkingContextInspectionProto.Types.Kind.NotRepository => new GitWorkingContextInspection.NotRepository(),
                Proto.GitWorkingContextInspectionProto.Types.Kind.ExecutableNotFound => new GitWorkingContextInspection.ExecutableNotFound(),
                Proto.GitWorkingContextInspectionProto.Types.Kind.Unavailable when proto.HasReason
                    => new GitWorkingContextInspection.Unavailable(proto.Reason),
                _ => throw new InvalidDataException("Deployed Git inspection data has an invalid kind.")
            };
        var snapshot = new GitWorkingContextSnapshot
        {
            Worktree = proto.Worktree,
            CommonDirectory = proto.CommonDirectory,
            Branch = proto.HasBranch ? proto.Branch : null,
            Detached = proto.Detached,
            Head = proto.HasHead ? proto.Head : null,
            Upstream = proto.HasUpstream ? proto.Upstream : null,
            Ahead = proto.Ahead,
            Behind = proto.Behind,
            Staged = proto.Staged,
            Modified = proto.Modified,
            Untracked = proto.Untracked,
            ChangedFiles = proto.ChangedFiles.ToImmutableHashSet(StringComparer.Ordinal)
        };
        _ = snapshot.GetHeadState();
        return new GitWorkingContextInspection.Available(snapshot);
    }

    internal static Proto.ChildRunTerminalProto ToLegacyTerminal(ChildRunTerminal terminal)
    {
        var result = terminal.Result;
        var proto = new Proto.ChildRunTerminalProto
        {
            Lost = terminal.Lost,
            Output = result.Output,
            Outcome = (int)result.Outcome,
            RunId = result.RunId?.Value ?? throw new InvalidDataException("A child terminal has no run ID."),
            ScopeId = result.ScopeId?.Value ?? throw new InvalidDataException("A child terminal has no scope ID."),
            AgentName = result.AgentName.Value,
            FindingsCount = result.FindingsCount
        };
        if (terminal.EvidenceWarning is not null) proto.EvidenceWarning = terminal.EvidenceWarning;
        if (result.OutcomeReason is not null) proto.OutcomeReason = result.OutcomeReason.Value.Value;
        if (result.WorkingContext is not null) proto.WorkingContext = ToLegacyWorkingDelta(result.WorkingContext);
        if (result.LogPath is not null) proto.LogPath = result.LogPath;
        if (result.ArtifactDirectory is not null) proto.ArtifactDirectory = result.ArtifactDirectory;
        foreach (var finding in result.Findings)
        {
            var item = new Proto.ChildFindingProto
            {
                Shape = (int)finding.Shape,
                Title = finding.Title,
                Content = finding.Content,
                Kind = finding.Kind,
                Sensitivity = (int)finding.Sensitivity,
                RecallMode = (int)finding.RecallMode,
                UpdateSemantics = finding.UpdateSemantics,
                Confidence = finding.Confidence,
                Durability = (int)finding.Durability,
                Reusability = (int)finding.Reusability
            };
            item.Evidence.AddRange(finding.Evidence);
            if (finding.FreshnessAtMs is { } freshness)
                item.FreshnessAtMs = freshness;
            proto.Findings.Add(item);
        }
        return proto;
    }

    internal static Proto.ChildWorkingContextProto ToLegacyWorkingDelta(WorkingContextDelta delta)
    {
        var proto = new Proto.ChildWorkingContextProto();
        if (delta.ProjectDirectory is not null) proto.ProjectDirectory = delta.ProjectDirectory;
        if (delta.Worktree is not null) proto.Worktree = delta.Worktree;
        if (delta.Branch is not null) proto.Branch = delta.Branch;
        if (delta.Head is not null) proto.Head = delta.Head;
        proto.ReadFiles.AddRange(delta.ReadFiles);
        proto.ConfirmedChangedFiles.AddRange(delta.ConfirmedChangedFiles);
        proto.ObservedChangedFiles.AddRange(delta.ObservedChangedFiles);
        return proto;
    }

    internal static string LegacyToolName(BackgroundChildRun run)
        => run.StartKey is ChildRunStartKey.Slash ? "skill_load" : run.SourceOperation;

    internal static string LegacyDeliveryArguments(BackgroundChildRun run)
        => JsonSerializer.Serialize(new { run_id = run.RunId.Value, source_operation = run.SourceOperation });

    internal static string LegacyTerminalBody(BackgroundChildRun run, ChildRunTerminal terminal,
        ChildRunCheckpoint? checkpoint)
    {
        var result = terminal.Result;
        var state = terminal.Lost ? "Lost" : result.Outcome switch
        {
            SubAgentRunOutcome.Completed => "Completed",
            SubAgentRunOutcome.Partial => "Partial",
            _ when result.OutcomeReason == SubAgentOutcomeReason.CancelledByParent => "Cancelled",
            _ => "Failed"
        };
        return JsonSerializer.Serialize(new
        {
            run_id = run.RunId.Value,
            scope_id = run.ScopeId.Value,
            state,
            source_operation = run.SourceOperation,
            outcome = result.Outcome.ToString(),
            reason = result.OutcomeReason?.Value,
            output = result.Output,
            working_context = result.WorkingContext,
            findings = result.Findings,
            log_path = result.LogPath,
            artifact_directory = result.ArtifactDirectory,
            warning = terminal.EvidenceWarning,
            checkpoint
        });
    }

    internal static SerializableChatMessage LegacyDeliveryCall(BackgroundChildRun run, ToolCallId callId) => new()
    {
        Role = ChatRole.Assistant,
        ToolCalls = [new SerializableToolCall
        {
            CallId = callId,
            Name = new ToolName(LegacyToolName(run)),
            ArgumentsJson = LegacyDeliveryArguments(run)
        }]
    };

    private static bool SameTerminalOutcome(ChildRunTerminal first, ChildRunTerminal second)
        => first.Lost == second.Lost && first.Result.Output == second.Result.Output
           && first.Result.Outcome == second.Result.Outcome && first.Result.OutcomeReason == second.Result.OutcomeReason
           && first.Result.RunId == second.Result.RunId && first.Result.ScopeId == second.Result.ScopeId
           && first.Result.AgentName == second.Result.AgentName;

    private static bool HasExactPair(IReadOnlyList<SerializableChatMessage> history, LegacyPreparedResult delivery)
    {
        var callId = delivery.Result.ToolCallId;
        var assistants = history.Where(message => message.ToolCalls.Any(call => call.CallId == callId)).ToArray();
        var results = history.Where(message => message.Role == ChatRole.Tool && message.ToolCallId == callId).ToArray();
        return assistants.Length == 1 && results.Length == 1
               && assistants[0].Role == ChatRole.Assistant
               && assistants[0].ToolCalls.SequenceEqual(delivery.Call.ToolCalls)
               && assistants[0].Content == delivery.Call.Content
               && assistants[0].Name == delivery.Call.Name
               && results[0].Name == delivery.Result.Name
               && results[0].Content == delivery.Result.Content
               && results[0].ToolCalls.Count == 0
               && results[0].MediaReferences.Count == 0;
    }

    internal static bool SameAuthority(TurnContextRecord first, TurnContextRecord second)
        => TurnContext.TryFromRecord(first, out var left, out _) && left is not null &&
           TurnContext.TryFromRecord(second, out var right, out _) && right is not null && left.HasSameAuthority(right);

    internal static bool SameContext(TurnContextRecord first, TurnContextRecord second)
        => first.SessionId == second.SessionId && first.TurnId == second.TurnId
           && first.Audience == second.Audience && first.Boundary == second.Boundary
           && first.ChannelType == second.ChannelType && first.RequesterSenderId == second.RequesterSenderId
           && first.RequesterPrincipal == second.RequesterPrincipal
           && first.TransportAuthenticity == second.TransportAuthenticity && first.PayloadTaint == second.PayloadTaint
           && first.SourceScope == second.SourceScope && first.SourceKind == second.SourceKind
           && first.DefaultDeliveryTarget == second.DefaultDeliveryTarget
           && first.RequestedDeliveryTarget == second.RequestedDeliveryTarget
           && first.HasAdoptedContext == second.HasAdoptedContext
           && first.HasThirdPartyAdoptedContext == second.HasThirdPartyAdoptedContext
           && first.AdoptedSpeakerIds.SequenceEqual(second.AdoptedSpeakerIds, StringComparer.Ordinal)
           && first.SupportsInteractiveApproval == second.SupportsInteractiveApproval;

    private static T CheckedEnum<T>(int value) where T : struct, Enum
        => Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value)
            : throw new InvalidDataException($"A deployed child record has an unknown {typeof(T).Name} value.");
}
