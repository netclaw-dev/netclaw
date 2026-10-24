// -----------------------------------------------------------------------
// <copyright file="NetclawProtoMapper.CoordinationV2.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Google.Protobuf;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Proto = Netclaw.Actors.Serialization.Proto;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Serialization;

internal static partial class NetclawProtoMapper
{
    // The legacy continuation is read-only data. A writer that sets it is a defect, so persistence fails loudly.
    private static void RejectLegacyContinuation(ChildContinuation? continuation)
    {
        if (continuation is not null)
            throw new InvalidDataException("A child continuation is legacy read-only data. No event can persist it.");
    }

    private static ChildContinuation FromProto(Proto.ChildContinuationProto proto) => new(
        new SubAgentRunId(proto.RunId), new ToolCallId(proto.DeliveryCallId));

    private static Proto.ChildNoticeProto ToProto(ChildNotice notice)
    {
        var proto = new Proto.ChildNoticeProto { RunId = notice.RunId.Value };
        if (notice.ReviewedWorkingContext is not null)
            proto.ReviewedWorkingContext = LegacyCoordinationReplay.ToLegacyWorkingDelta(notice.ReviewedWorkingContext);
        return proto;
    }

    private static ChildNotice FromProto(Proto.ChildNoticeProto proto) => new(
        new SubAgentRunId(proto.RunId),
        proto.ReviewedWorkingContext is null ? null
            : LegacyCoordinationReplay.FromLegacyWorkingDelta(proto.ReviewedWorkingContext));

    internal static Proto.SessionSnapshotV2Proto ToProtoV2(SessionSnapshot snapshot)
    {
        var proto = new Proto.SessionSnapshotV2Proto { State = ToProto(snapshot) };
        proto.ChildRuns.AddRange(snapshot.ChildRuns.Select(ToProto));
        proto.LegacyCoordinationEvidence.AddRange(snapshot.LegacyCoordinationEvidence.Select(ToProto));
        return proto;
    }

    internal static SessionSnapshot FromProto(Proto.SessionSnapshotV2Proto proto)
    {
        if (proto.State is null)
            throw new InvalidDataException("A v2 session snapshot has no state record.");
        var state = FromProto(proto.State);
        return state with
        {
            ChildRuns = proto.ChildRuns.Select(FromProto).ToArray(),
            LegacyCoordinationEvidence = proto.LegacyCoordinationEvidence.Select(FromProto).ToArray()
        };
    }

    internal static SessionSnapshot FromLegacySnapshot(Proto.SessionSnapshotProto proto, byte[] rawPayload)
    {
        var childInputIds = proto.PendingInputs.Where(input => input.HasSourceChildRunId)
            .Select(input => new InputId(input.InputId)).ToHashSet();
        var decoded = FromProto(proto);
        var snapshot = decoded with { PendingInputs = decoded.PendingInputs.Where(input => !childInputIds.Contains(input.InputId)).ToArray() };
        var legacyRuns = proto.ChildRuns.Select(run => FromLegacyChildRun(run)).ToArray();
        var evidence = new List<LegacyCoordinationEvidence>(snapshot.LegacyCoordinationEvidence);
        var adoptedIds = proto.AdoptedTaskInputIds.Select(id => new InputId(id)).ToArray();
        var adoptedInputs = snapshot.PendingInputs.Where(input => adoptedIds.Contains(input.InputId)).ToArray();
        if (adoptedInputs.Length > 0)
        {
            var context = proto.AdoptedTaskContext is null ? null : FromLegacyTurnContext(proto.AdoptedTaskContext);
            var valid = context is not null && adoptedInputs.Select(input => input.InputId).SequenceEqual(adoptedIds)
                && LegacyCoordinationReplay.SameContext(context, adoptedInputs[^1].TurnContext)
                && adoptedInputs.All(input => LegacyCoordinationReplay.SameAuthority(context, input.TurnContext));
            evidence.Add(new LegacyCoordinationEvidence
            {
                Manifest = "ss-v1-adopted-task",
                Payload = valid ? [] : [.. rawPayload],
                Disposition = valid ? LegacyCoordinationDisposition.AppliedOrdinaryAdoption
                    : LegacyCoordinationDisposition.QuarantinedInvalidAdoption,
                AdoptedContext = context,
                InputIds = adoptedIds,
                ActiveInputIds = adoptedIds,
                Reason = valid ? null : "The deployed snapshot adoption differs from its pending ordinary input authority."
            });
        }

        var rawSnapshotRetained = evidence.Any(item => item.Payload.Length > 0);
        var activeRunIds = new HashSet<SubAgentRunId>();
        for (var index = 0; index < proto.ChildRuns.Count; index++)
        {
            var oldRun = proto.ChildRuns[index];
            var run = legacyRuns[index];
            var runId = new SubAgentRunId(oldRun.RunId);
            var inputIds = oldRun.HasDeliveryInputId
                ? new[] { new InputId(oldRun.DeliveryInputId) }
                : Array.Empty<InputId>();
            var approvals = oldRun.Approvals.SelectMany(approval =>
            {
                var events = new List<SessionProtocol.ISessionEvent>(2);
                if (approval.Request is not null) events.Add(FromProto(approval.Request));
                if (approval.Resolution is not null) events.Add(FromProto(approval.Resolution));
                return events;
            }).ToArray();
            if (oldRun.HasDeliveryInputId
                && TryGetLegacySnapshotDelivery(snapshot, proto, oldRun, run, out var delivery,
                    out var hasHistoryPair, out var hasPendingInput))
            {
                var activeParentTask = hasHistoryPair
                    && (hasPendingInput || proto.AdoptedTaskInputIds.Contains(oldRun.DeliveryInputId));
                var terminalResult = ((ChildRunLifecycle.Terminal)run.Lifecycle).Result;
                // A pair that the parent never read stays prepared. The recovery reports it as a notice.
                legacyRuns[index] = run with
                {
                    InitialWorkingSnapshot = null,
                    Lifecycle = !hasHistoryPair
                        ? new ChildRunLifecycle.Terminal(terminalResult) { LegacyPrepared = delivery }
                        : new ChildRunLifecycle.Reported(terminalResult.Result.Outcome, terminalResult.Result.OutcomeReason,
                            new ChildRunReport.LegacyDelivery(delivery.Result.ToolCallId
                                ?? throw new InvalidDataException("A deployed child delivery has no call identity.")))
                };
                if (activeParentTask)
                    activeRunIds.Add(runId);
                if (hasHistoryPair && !activeParentTask)
                    continue;
                evidence.Add(new LegacyCoordinationEvidence
                {
                    Manifest = "ss-v1",
                    Payload = [],
                    Disposition = LegacyCoordinationDisposition.AppliedChildContinuation,
                    InputIds = inputIds,
                    ActiveInputIds = inputIds,
                    AdoptedContext = run.OriginalContext,
                    ChildRunId = runId,
                    DeliveryCallId = delivery.Result.ToolCallId,
                    ApprovalEvents = approvals
                });
                continue;
            }

            var hasAmbiguousDelivery = oldRun.HasDeliveryInputId;
            if (!hasAmbiguousDelivery && approvals.Length == 0)
                continue;
            evidence.Add(new LegacyCoordinationEvidence
            {
                Manifest = "ss-v1",
                Payload = hasAmbiguousDelivery && !rawSnapshotRetained ? [.. rawPayload] : [],
                Disposition = hasAmbiguousDelivery
                    ? LegacyCoordinationDisposition.UnresolvedLegacyChild
                    : LegacyCoordinationDisposition.PreservedLegacyFacts,
                InputIds = inputIds,
                ActiveInputIds = inputIds,
                AdoptedContext = oldRun.OriginalContext is null
                    ? null : FromLegacyTurnContext(oldRun.OriginalContext),
                Reason = hasAmbiguousDelivery
                    ? "The deployed snapshot has no structured delivery call/result pair. The delivery remains unresolved."
                    : null,
                ChildRunId = runId,
                ApprovalEvents = approvals
            });
            rawSnapshotRetained |= hasAmbiguousDelivery;
        }

        foreach (var input in proto.PendingInputs.Where(input => input.HasSourceChildRunId))
        {
            var inputId = new InputId(input.InputId);
            if (evidence.Any(item => item.InputIds.Contains(inputId)))
                continue;
            evidence.Add(new LegacyCoordinationEvidence
            {
                Manifest = "ss-v1", Payload = rawSnapshotRetained ? [] : [.. rawPayload],
                Disposition = LegacyCoordinationDisposition.UnresolvedLegacyChild,
                InputIds = [inputId], ActiveInputIds = [inputId],
                ChildRunId = new SubAgentRunId(input.SourceChildRunId),
                AdoptedContext = input.TurnContext is null ? null : FromLegacyTurnContext(input.TurnContext),
                Reason = "The deployed snapshot child input has no exact unsettled delivery correlation."
            });
            rawSnapshotRetained = true;
        }
        var activeRuns = legacyRuns.Where(run => activeRunIds.Contains(run.RunId)).ToArray();
        if (activeRuns.Length > 1)
        {
            foreach (var run in activeRuns)
            {
                evidence.Add(new LegacyCoordinationEvidence
                {
                    Manifest = "ss-v1", Payload = rawSnapshotRetained ? [] : [.. rawPayload],
                    Disposition = LegacyCoordinationDisposition.UnresolvedLegacyChild,
                    ChildRunId = run.RunId, AdoptedContext = run.OriginalContext,
                    Reason = "The deployed snapshot has more than one active child continuation."
                });
                rawSnapshotRetained = true;
            }
        }

        return snapshot with
        {
            ChildRuns = legacyRuns,
            LegacyCoordinationEvidence = evidence
        };
    }

    private static bool TryGetLegacySnapshotDelivery(SessionSnapshot snapshot,
        Proto.SessionSnapshotProto snapshotProto, Proto.BackgroundChildRunProto oldRun,
        BackgroundChildRun run, out LegacyPreparedResult delivery, out bool hasHistoryPair,
        out bool hasPendingInput)
    {
        delivery = null!;
        hasHistoryPair = false;
        hasPendingInput = false;
        if (run.Lifecycle is not ChildRunLifecycle.Terminal terminal)
            return false;
        var toolName = LegacyCoordinationReplay.LegacyToolName(run);
        var expectedBody = LegacyCoordinationReplay.LegacyTerminalBody(run, terminal.Result,
            oldRun.ChildCheckpoint is null ? null : LegacyCoordinationReplay.FromLegacyCheckpoint(oldRun.ChildCheckpoint));
        var pendingProto = snapshotProto.PendingInputs.SingleOrDefault(input => input.InputId == oldRun.DeliveryInputId);
        SerializableChatMessage? pendingResult = null;
        if (pendingProto is not null)
        {
            if (!pendingProto.HasSourceChildRunId || pendingProto.SourceChildRunId != run.RunId.Value
                || pendingProto.UserMessage?.ToolCallId is not { } pendingCallId
                || run.StartKey is ChildRunStartKey.Tool original && original.CallId.Value == pendingCallId)
                return false;
            pendingResult = FromProto(pendingProto.UserMessage);
            var context = pendingProto.TurnContext is null ? null : FromLegacyTurnContext(pendingProto.TurnContext);
            if (pendingProto.SessionId?.Value != run.StartKey.SessionId.Value
                || context is null || !LegacyCoordinationReplay.SameContext(context, run.OriginalContext)
                || pendingResult.Role != ChatRole.Tool || pendingResult.Name != toolName
                || pendingResult.Content != expectedBody)
                return false;
        }

        var expectedArguments = LegacyCoordinationReplay.LegacyDeliveryArguments(run);
        var historyCalls = snapshot.History.SelectMany(message => message.ToolCalls)
            .Where(call => call.Name.Value == toolName && call.ArgumentsJson == expectedArguments)
            .ToArray();
        if (historyCalls.Length > 1)
            return false;
        if (historyCalls.Length == 1)
        {
            var callId = historyCalls[0].CallId;
            if (snapshot.History.SelectMany(message => message.ToolCalls)
                    .Count(call => call.CallId == callId) != 1)
                return false;
            var matchingResults = snapshot.History.Where(message => message.Role == ChatRole.Tool
                && message.ToolCallId == callId && message.Name == toolName && message.Content == expectedBody).ToArray();
            if (matchingResults.Length != 1 || (pendingResult is not null
                    && (pendingResult.ToolCallId != callId || pendingResult.Role != matchingResults[0].Role
                        || pendingResult.Name != matchingResults[0].Name || pendingResult.Content != matchingResults[0].Content)))
                return false;

            var canonicalCall = LegacyCoordinationReplay.LegacyDeliveryCall(run, callId);
            delivery = new LegacyPreparedResult(canonicalCall, matchingResults[0], null);
            hasHistoryPair = true;
            hasPendingInput = pendingProto is not null;
            return true;
        }

        if (pendingResult?.ToolCallId is not { } preparedCallId)
            return false;
        var pendingCall = LegacyCoordinationReplay.LegacyDeliveryCall(run, preparedCallId);
        delivery = new LegacyPreparedResult(pendingCall, pendingResult, null);
        hasPendingInput = true;
        return true;
    }

    private static BackgroundChildRun FromLegacyChildRun(Proto.BackgroundChildRunProto proto)
    {
        if (proto.OriginalContext is null || proto.InitialWorkingContext is null || proto.InitialGit is null)
            throw new InvalidDataException("A deployed child snapshot lacks owner or initial context.");
        var session = FromProto(new Proto.SessionIdProto { Value = proto.OwnerSessionId });
        ChildRunStartKey startKey = proto.ActivationCase switch
        {
            Proto.BackgroundChildRunProto.ActivationOneofCase.ToolCallId =>
                new ChildRunStartKey.Tool(session, proto.OriginalTurnId, new ToolCallId(proto.ToolCallId)),
            Proto.BackgroundChildRunProto.ActivationOneofCase.SlashInputId =>
                new ChildRunStartKey.Slash(session, proto.OriginalTurnId, new InputId(proto.SlashInputId)),
            _ => throw new InvalidDataException("A deployed child snapshot has no activation identity.")
        };
        var terminal = proto.PreparedTerminal ?? proto.Terminal;
        ChildRunLifecycle lifecycle = terminal is not null
            ? new ChildRunLifecycle.Terminal(FromLegacyTerminal(terminal))
            : proto.HasCancellationRequestedAtMs
                ? new ChildRunLifecycle.Cancelling(proto.ChildCheckpoint is null
                    ? null : FromLegacyCheckpoint(proto.ChildCheckpoint))
                : new ChildRunLifecycle.Accepted(proto.ChildCheckpoint is null
                    ? null : FromLegacyCheckpoint(proto.ChildCheckpoint));
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId(proto.RunId),
            ScopeId = new SubAgentScopeId(proto.ScopeId),
            StartKey = startKey,
            AgentName = new AgentName(proto.AgentName),
            SourceOperation = proto.SourceOperation,
            ArgumentsDigest = proto.ArgumentsDigest,
            OriginalContext = FromLegacyTurnContext(proto.OriginalContext),
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = FromProto(proto.InitialWorkingContext),
                Git = FromLegacyGitInspection(proto.InitialGit)
            },
            AcceptedAtMs = proto.AcceptedAtMs,
            Lifecycle = lifecycle,
            // A deployed v1 child record has no reminder link.
            SourceReminderId = null
        };
    }

    internal static Proto.ChildRunAcceptedV2Proto ToProto(ChildRunAccepted evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        Run = ToProto(evt.Run),
        AcceptanceResult = evt.AcceptanceResult is null ? null : ToProto(evt.AcceptanceResult),
        RecordedAtMs = evt.RecordedAtMs
    };

    internal static ChildRunAccepted FromProto(Proto.ChildRunAcceptedV2Proto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        Run = FromProto(proto.Run ?? throw new InvalidDataException("A v2 child acceptance has no run.")),
        AcceptanceResult = proto.AcceptanceResult is null ? null : FromProto(proto.AcceptanceResult),
        RecordedAtMs = proto.RecordedAtMs
    };

    internal static Proto.ChildRunEventV2Proto ToProto(ChildRunEvent evt)
    {
        var proto = new Proto.ChildRunEventV2Proto
        {
            SessionId = ToProto(evt.SessionId),
            RunId = evt.RunId.Value,
            RecordedAtMs = evt.RecordedAtMs
        };
        switch (evt)
        {
            case ChildRunEvent.Checkpointed checkpoint:
                proto.Checkpoint = ToProto(checkpoint.Checkpoint);
                break;
            case ChildRunEvent.CancellationRequested:
                proto.CancellationRequested = true;
                break;
            case ChildRunEvent.TerminalRecorded terminal:
                proto.Terminal = LegacyCoordinationReplay.ToLegacyTerminal(terminal.Terminal);
                break;
            case ChildRunEvent.Parked parked:
                proto.Parked = ToProto(parked.Park, parked.Requests.Select(static request => new ChildParkRequest(request, null)));
                break;
            case ChildRunEvent.Resumed:
                proto.Resumed = true;
                break;
            case ChildRunEvent.ResultPrepared:
                throw new InvalidDataException("A prepared child result is legacy read-only data. No writer persists it.");
            default:
                throw new InvalidDataException("A v2 child event has an unsupported variant.");
        }
        return proto;
    }

    internal static ChildRunEvent FromProto(Proto.ChildRunEventV2Proto proto)
    {
        var sessionId = FromProto(proto.SessionId);
        var runId = new SubAgentRunId(proto.RunId);
        return proto.FactCase switch
        {
            Proto.ChildRunEventV2Proto.FactOneofCase.Checkpoint => new ChildRunEvent.Checkpointed(
                FromLegacyCheckpoint(proto.Checkpoint))
            {
                SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs
            },
            Proto.ChildRunEventV2Proto.FactOneofCase.CancellationRequested when proto.CancellationRequested
                => new ChildRunEvent.CancellationRequested
                {
                    SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs
                },
            Proto.ChildRunEventV2Proto.FactOneofCase.Terminal => new ChildRunEvent.TerminalRecorded(
                FromLegacyTerminal(proto.Terminal))
            {
                SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs
            },
            Proto.ChildRunEventV2Proto.FactOneofCase.ResultPrepared when proto.ResultPrepared.Call is not null
                && proto.ResultPrepared.Result is not null => new ChildRunEvent.ResultPrepared(
                    FromProto(proto.ResultPrepared.Call), FromProto(proto.ResultPrepared.Result),
                    proto.ResultPrepared.ReviewedWorkingContext is null ? null
                        : LegacyCoordinationReplay.FromLegacyWorkingDelta(proto.ResultPrepared.ReviewedWorkingContext))
                {
                    SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs
                },
            Proto.ChildRunEventV2Proto.FactOneofCase.Parked => new ChildRunEvent.Parked(FromProto(proto.Parked),
                ReadParkRequests(proto.Parked).Select(static request => request.Request).ToArray())
            {
                SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs
            },
            Proto.ChildRunEventV2Proto.FactOneofCase.Resumed when proto.Resumed => new ChildRunEvent.Resumed
            {
                SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs
            },
            _ => throw new InvalidDataException("A v2 child event has no valid fact.")
        };
    }

    // durable-child-park-recovery: the park and its requests. The owner measures this record for the size cap.
    internal static Proto.ChildRunParkProto ToProto(ChildRunPark park, IEnumerable<ChildParkRequest> requests)
    {
        var proto = new Proto.ChildRunParkProto
        {
            LoopGuard = ToProto(park.LoopGuard),
            FileActivity = LegacyCoordinationReplay.ToLegacyWorkingDelta(park.FileActivity)
        };
        proto.Transcript.AddRange(park.Transcript.Select(ToProto));
        if (park.ScopeDirectories.ProjectDirectory is { } projectDirectory)
            proto.ProjectDirectory = projectDirectory;
        if (park.ScopeDirectories.InheritedWorkingDirectory is { } inherited)
            proto.InheritedWorkingDirectory = inherited;
        if (park.SkillOverlay is { } overlay)
            proto.SkillOverlay = overlay;
        proto.Requests.AddRange(requests.Select(static request => new Proto.ChildRunApprovalProto
        {
            Request = ToProto(request.Request),
            Resolution = request.Resolution is null ? null : ToProto(request.Resolution)
        }));
        return proto;
    }

    private static ChildRunPark FromProto(Proto.ChildRunParkProto proto) => new()
    {
        Transcript = proto.Transcript.Select(FromProto).ToArray(),
        LoopGuard = FromProto(proto.LoopGuard ?? throw new InvalidDataException("A child park has no loop guard.")),
        ScopeDirectories = new ChildParkScopeDirectories(proto.HasProjectDirectory ? proto.ProjectDirectory : null,
            proto.HasInheritedWorkingDirectory ? proto.InheritedWorkingDirectory : null),
        FileActivity = LegacyCoordinationReplay.FromLegacyWorkingDelta(proto.FileActivity
            ?? throw new InvalidDataException("A child park has no file activity.")),
        SkillOverlay = proto.HasSkillOverlay ? proto.SkillOverlay : null
    };

    private static ImmutableArray<ChildParkRequest> ReadParkRequests(Proto.ChildRunParkProto proto)
        => [.. proto.Requests.Select(static approval => new ChildParkRequest(
            FromProto(approval.Request ?? throw new InvalidDataException("A child park request has no request.")),
            approval.Resolution is null ? null : FromProto(approval.Resolution)))];

    private static Proto.ChildLoopGuardProto ToProto(TurnLoopGuardState loopGuard)
    {
        var proto = new Proto.ChildLoopGuardProto
        {
            ToolIterationCount = loopGuard.ToolIterationCount,
            ToolCallCount = loopGuard.ToolCallCount,
            RefusedRepeatCount = loopGuard.RefusedRepeatCount
        };
        if (loopGuard.LastBlockedAction is { } blocked)
            proto.LastBlockedAction = blocked.Value;
        proto.CompletedCycles.AddRange(loopGuard.CompletedCycles.Select(static cycle =>
            new Proto.ChildLoopGuardProto.Types.Cycle { Action = cycle.Action.Value, Outcome = cycle.OutcomeValue }));
        proto.Episodes.AddRange(loopGuard.Episodes.Select(static pair => new Proto.ChildLoopGuardProto.Types.Episode
        {
            ToolName = pair.Key.ToolName.Value, ArgumentsHash = pair.Key.ArgumentsHash, Outcome = pair.Value.Outcome,
            EqualRounds = pair.Value.EqualRounds, Corrected = pair.Value.Corrected, PendingJob = pair.Value.PendingJob
        }));
        return proto;
    }

    private static TurnLoopGuardState FromProto(Proto.ChildLoopGuardProto proto) => new(
        proto.ToolIterationCount,
        proto.ToolCallCount,
        proto.CompletedCycles.Select(static cycle => (new ToolActionSignature(cycle.Action), cycle.Outcome)).ToArray(),
        proto.HasLastBlockedAction ? new ToolActionSignature(proto.LastBlockedAction) : null,
        proto.Episodes.ToDictionary(
            static episode => new ToolRecurrenceKey(new ToolName(episode.ToolName), episode.ArgumentsHash),
            static episode => new ToolRecurrenceEpisode(episode.Outcome, episode.EqualRounds, episode.Corrected, episode.PendingJob)),
        proto.RefusedRepeatCount);

    private static Proto.BackgroundChildRunV2Proto ToProto(BackgroundChildRun run)
    {
        var needsInitialSnapshot = NeedsInitialSnapshot(run.Lifecycle);
        if (needsInitialSnapshot != (run.InitialWorkingSnapshot is not null))
            throw new InvalidDataException("A v2 child snapshot has an initial working snapshot that conflicts with its lifecycle.");

        var proto = new Proto.BackgroundChildRunV2Proto
        {
            RunId = run.RunId.Value,
            ScopeId = run.ScopeId.Value,
            OwnerSessionId = run.StartKey.SessionId.Value,
            OriginalTurnId = run.StartKey.TurnId,
            AgentName = run.AgentName.Value,
            SourceOperation = run.SourceOperation,
            ArgumentsDigest = run.ArgumentsDigest,
            OriginalContext = ToProto(run.OriginalContext),
            AcceptedAtMs = run.AcceptedAtMs,
            PromptsIssued = run.PromptsIssued
        };
        if (run.SourceReminderId is { } sourceReminder)
            proto.SourceReminderId = sourceReminder.Value;
        if (run.InitialWorkingSnapshot is { } initialSnapshot)
        {
            proto.InitialWorkingContext = ToProto(initialSnapshot.WorkingContext);
            proto.InitialGit = ToProtoGitInspection(initialSnapshot.Git);
        }
        switch (run.StartKey)
        {
            case ChildRunStartKey.Tool tool:
                proto.ToolCallId = tool.CallId.Value;
                break;
            case ChildRunStartKey.Slash slash:
                proto.SlashInputId = slash.InputId.Value;
                break;
            default:
                throw new InvalidDataException("A v2 child snapshot has an unknown start key.");
        }
        switch (run.Lifecycle)
        {
            case ChildRunLifecycle.Accepted accepted:
                proto.Accepted = new Proto.ChildRunAcceptedStateV2Proto();
                if (accepted.Checkpoint is not null)
                    proto.Accepted.Checkpoint = ToProto(accepted.Checkpoint);
                break;
            case ChildRunLifecycle.Cancelling cancelling:
                proto.Cancelling = new Proto.ChildRunAcceptedStateV2Proto();
                if (cancelling.Checkpoint is not null)
                    proto.Cancelling.Checkpoint = ToProto(cancelling.Checkpoint);
                break;
            case ChildRunLifecycle.Terminal { LegacyPrepared: null } terminal:
                proto.Terminal = LegacyCoordinationReplay.ToLegacyTerminal(terminal.Result);
                break;
            case ChildRunLifecycle.Terminal { LegacyPrepared: { } prepared } terminal:
                proto.Prepared = ToProto(terminal.Result, prepared);
                break;
            case ChildRunLifecycle.Parked parked:
                proto.Parked = ToProto(parked.Park, parked.Requests);
                break;
            case ChildRunLifecycle.Reported reported:
                proto.Closed = new Proto.ChildRunClosedV2Proto { Outcome = (int)reported.Outcome };
                if (reported.Reason is not null)
                    proto.Closed.Reason = reported.Reason.Value.Value;
                switch (reported.Report)
                {
                    case ChildRunReport.Notice notice:
                        proto.Closed.NoticeInputId = notice.NoticeInputId.Value;
                        break;
                    case ChildRunReport.LegacyDelivery delivery:
                        proto.Closed.DeliveryCallId = delivery.DeliveryCallId.Value;
                        break;
                    default:
                        throw new InvalidDataException("A v2 child snapshot has an unknown report.");
                }
                break;
            default:
                throw new InvalidDataException("A v2 child snapshot has an unknown lifecycle.");
        }
        return proto;
    }

    private static BackgroundChildRun FromProto(Proto.BackgroundChildRunV2Proto proto)
    {
        if (proto.OriginalContext is null)
            throw new InvalidDataException("A v2 child snapshot lacks authority context.");
        var session = FromProto(new Proto.SessionIdProto { Value = proto.OwnerSessionId });
        ChildRunStartKey startKey = proto.ActivationCase switch
        {
            Proto.BackgroundChildRunV2Proto.ActivationOneofCase.ToolCallId =>
                new ChildRunStartKey.Tool(session, proto.OriginalTurnId, new ToolCallId(proto.ToolCallId)),
            Proto.BackgroundChildRunV2Proto.ActivationOneofCase.SlashInputId =>
                new ChildRunStartKey.Slash(session, proto.OriginalTurnId, new InputId(proto.SlashInputId)),
            _ => throw new InvalidDataException("A v2 child snapshot has no activation identity.")
        };
        ChildRunLifecycle lifecycle = proto.LifecycleCase switch
        {
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Accepted =>
                new ChildRunLifecycle.Accepted(proto.Accepted.Checkpoint is null
                    ? null : FromLegacyCheckpoint(proto.Accepted.Checkpoint)),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Cancelling =>
                new ChildRunLifecycle.Cancelling(proto.Cancelling.Checkpoint is null
                    ? null : FromLegacyCheckpoint(proto.Cancelling.Checkpoint)),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Terminal =>
                new ChildRunLifecycle.Terminal(FromLegacyTerminal(proto.Terminal)),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Prepared => ReadPrepared(proto.Prepared),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Consumed => ReadConsumed(proto.Consumed),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Closed => ReadReported(proto.Closed),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Parked =>
                new ChildRunLifecycle.Parked(FromProto(proto.Parked), ReadParkRequests(proto.Parked)),
            _ => throw new InvalidDataException("A v2 child snapshot has no lifecycle.")
        };
        var needsInitialSnapshot = NeedsInitialSnapshot(lifecycle);
        if (needsInitialSnapshot != (proto.InitialWorkingContext is not null && proto.InitialGit is not null)
            || (proto.InitialWorkingContext is null) != (proto.InitialGit is null))
            throw new InvalidDataException("A v2 child snapshot has an initial working snapshot that conflicts with its lifecycle.");
        WorkingContextSnapshot? initialWorkingSnapshot = null;
        if (needsInitialSnapshot)
        {
            var initialWorkingContext = proto.InitialWorkingContext
                ?? throw new InvalidDataException("A v2 child snapshot lacks its initial working context.");
            var initialGit = proto.InitialGit
                ?? throw new InvalidDataException("A v2 child snapshot lacks its initial Git context.");
            initialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = FromProto(initialWorkingContext),
                Git = FromLegacyGitInspection(initialGit)
            };
        }
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId(proto.RunId),
            ScopeId = new SubAgentScopeId(proto.ScopeId),
            StartKey = startKey,
            AgentName = new AgentName(proto.AgentName),
            SourceOperation = proto.SourceOperation,
            ArgumentsDigest = proto.ArgumentsDigest,
            OriginalContext = FromLegacyTurnContext(proto.OriginalContext),
            InitialWorkingSnapshot = initialWorkingSnapshot,
            AcceptedAtMs = proto.AcceptedAtMs,
            Lifecycle = lifecycle,
            SourceReminderId = proto.HasSourceReminderId ? new ReminderId(proto.SourceReminderId) : null,
            PromptsIssued = proto.PromptsIssued
        };
    }

    // A run keeps its initial working snapshot until the owner can still enrich its result. A run that a deployed
    // runtime already prepared has no snapshot, and a reported run has none either.
    private static bool NeedsInitialSnapshot(ChildRunLifecycle lifecycle)
        => lifecycle is ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling or ChildRunLifecycle.Parked
            or ChildRunLifecycle.Terminal { LegacyPrepared: null };

    private static Proto.ChildPreparedStateV2Proto ToProto(ChildRunTerminal terminal, LegacyPreparedResult prepared) => new()
    {
        Terminal = LegacyCoordinationReplay.ToLegacyTerminal(terminal),
        Call = ToProto(prepared.Call),
        Result = ToProto(prepared.Result),
        ReviewedWorkingContext = prepared.ReviewedWorkingContext is null ? null
            : LegacyCoordinationReplay.ToLegacyWorkingDelta(prepared.ReviewedWorkingContext)
    };

    private static (ChildRunTerminal Terminal, LegacyPreparedResult Prepared) ReadPreparedState(Proto.ChildPreparedStateV2Proto proto)
    {
        if (proto.Terminal is null || proto.Call is null || proto.Result is null)
            throw new InvalidDataException("A v2 prepared child result lacks its exact pair.");
        return (FromLegacyTerminal(proto.Terminal), new LegacyPreparedResult(
            FromProto(proto.Call), FromProto(proto.Result), proto.ReviewedWorkingContext is null ? null
                : LegacyCoordinationReplay.FromLegacyWorkingDelta(proto.ReviewedWorkingContext)));
    }

    // Snapshot case 17: a result that the parent never read. The owner reports it as a notice at recovery.
    private static ChildRunLifecycle ReadPrepared(Proto.ChildPreparedStateV2Proto proto)
    {
        var (terminal, prepared) = ReadPreparedState(proto);
        return new ChildRunLifecycle.Terminal(terminal) { LegacyPrepared = prepared };
    }

    // Snapshot case 18: the pair is already in the snapshot history, so the run is reported.
    private static ChildRunLifecycle ReadConsumed(Proto.ChildPreparedStateV2Proto proto)
    {
        var (terminal, prepared) = ReadPreparedState(proto);
        return new ChildRunLifecycle.Reported(terminal.Result.Outcome, terminal.Result.OutcomeReason,
            new ChildRunReport.LegacyDelivery(prepared.Result.ToolCallId
                ?? throw new InvalidDataException("A v2 consumed child result has no call identity.")));
    }

    // Snapshot case 19: a deployed delivery names a call. A notice names an input.
    private static ChildRunLifecycle ReadReported(Proto.ChildRunClosedV2Proto proto)
        => new ChildRunLifecycle.Reported(
            CheckedEnum<SubAgentRunOutcome>(proto.Outcome),
            proto.HasReason ? new SubAgentOutcomeReason(proto.Reason) : null,
            proto.HasNoticeInputId
                ? new ChildRunReport.Notice(new InputId(proto.NoticeInputId))
                : string.IsNullOrEmpty(proto.DeliveryCallId)
                    ? throw new InvalidDataException("A v2 reported child result names neither a notice nor a delivery.")
                    : new ChildRunReport.LegacyDelivery(new ToolCallId(proto.DeliveryCallId)));

    private static Proto.ChildRunCheckpointProto ToProto(ChildRunCheckpoint checkpoint) => new()
    {
        CompletedRound = checkpoint.CompletedRound,
        Summary = checkpoint.Summary,
        ConfirmedActivity = LegacyCoordinationReplay.ToLegacyWorkingDelta(checkpoint.ConfirmedActivity)
    };

    private static ChildRunCheckpoint FromLegacyCheckpoint(Proto.ChildRunCheckpointProto proto)
        => LegacyCoordinationReplay.FromLegacyCheckpoint(proto);

    private static ChildRunTerminal FromLegacyTerminal(Proto.ChildRunTerminalProto proto)
        => LegacyCoordinationReplay.FromLegacyTerminal(proto);

    private static GitWorkingContextInspection FromLegacyGitInspection(Proto.GitWorkingContextInspectionProto proto)
        => LegacyCoordinationReplay.FromLegacyGitInspection(proto);

    private static Proto.LegacyCoordinationEvidenceProto ToProto(LegacyCoordinationEvidence evidence)
    {
        var proto = new Proto.LegacyCoordinationEvidenceProto
        {
            Manifest = evidence.Manifest,
            Payload = ByteString.CopyFrom(evidence.Payload.AsSpan()),
            Disposition = (int)evidence.Disposition,
            LegacyDispatchClosed = evidence.LegacyDispatchClosed
        };
        proto.InputIds.AddRange(evidence.InputIds.Select(static id => id.Value));
        proto.ActiveInputIds.AddRange(evidence.ActiveInputIds.Select(static id => id.Value));
        if (evidence.AdoptedContext is not null)
            proto.AdoptedContext = ToProto(evidence.AdoptedContext);
        if (evidence.Reason is not null)
            proto.Reason = evidence.Reason;
        if (evidence.ChildRunId is not null)
            proto.ChildRunId = evidence.ChildRunId.Value.Value;
        if (evidence.DeliveryCallId is not null)
            proto.DeliveryCallId = evidence.DeliveryCallId.Value.Value;
        if (evidence.LegacyCheckpoint is not null)
            proto.LegacyCheckpoint = ToProto(evidence.LegacyCheckpoint);
        if (evidence.LegacyTerminalSequenceNr is not null)
            proto.LegacyTerminalSequenceNr = evidence.LegacyTerminalSequenceNr.Value;
        proto.ApprovalRequests.AddRange(evidence.ApprovalEvents.OfType<ToolApprovalRequested>().Select(ToProto));
        proto.ApprovalResolutions.AddRange(evidence.ApprovalEvents.OfType<ToolApprovalResolved>().Select(ToProto));
        return proto;
    }

    private static LegacyCoordinationEvidence FromProto(Proto.LegacyCoordinationEvidenceProto proto)
    {
        var disposition = CheckedEnum<LegacyCoordinationDisposition>(proto.Disposition);
        return new LegacyCoordinationEvidence
        {
            Manifest = proto.Manifest,
            Payload = [.. proto.Payload.Span],
            Disposition = disposition,
            InputIds = proto.InputIds.Select(static id => new InputId(id)).ToArray(),
            ActiveInputIds = proto.ActiveInputIds.Select(static id => new InputId(id)).ToArray(),
            AdoptedContext = proto.AdoptedContext is null ? null : FromLegacyTurnContext(proto.AdoptedContext),
            Reason = proto.HasReason ? proto.Reason : null,
            ChildRunId = proto.HasChildRunId ? new SubAgentRunId(proto.ChildRunId) : null,
            DeliveryCallId = proto.HasDeliveryCallId ? new ToolCallId(proto.DeliveryCallId) : null,
            LegacyDispatchClosed = proto.LegacyDispatchClosed,
            LegacyCheckpoint = proto.LegacyCheckpoint is null ? null
                : LegacyCoordinationReplay.FromLegacyCheckpoint(proto.LegacyCheckpoint),
            LegacyTerminalSequenceNr = proto.HasLegacyTerminalSequenceNr ? proto.LegacyTerminalSequenceNr : null,
            ApprovalEvents = proto.ApprovalRequests.Select(static approval => (ISessionEvent)FromProto(approval))
                .Concat(proto.ApprovalResolutions.Select(static approval => (ISessionEvent)FromProto(approval))).ToArray()
        };
    }

    private static Proto.GitWorkingContextInspectionProto ToProtoGitInspection(GitWorkingContextInspection inspection)
    {
        var proto = new Proto.GitWorkingContextInspectionProto();
        switch (inspection)
        {
            case GitWorkingContextInspection.Skipped:
                proto.Kind = Proto.GitWorkingContextInspectionProto.Types.Kind.Skipped;
                break;
            case GitWorkingContextInspection.NotRepository:
                proto.Kind = Proto.GitWorkingContextInspectionProto.Types.Kind.NotRepository;
                break;
            case GitWorkingContextInspection.ExecutableNotFound:
                proto.Kind = Proto.GitWorkingContextInspectionProto.Types.Kind.ExecutableNotFound;
                break;
            case GitWorkingContextInspection.Unavailable unavailable:
                proto.Kind = Proto.GitWorkingContextInspectionProto.Types.Kind.Unavailable;
                proto.Reason = unavailable.Reason;
                break;
            case GitWorkingContextInspection.Available { Snapshot: var git }:
                _ = git.GetHeadState();
                proto.Kind = Proto.GitWorkingContextInspectionProto.Types.Kind.Available;
                proto.Worktree = git.Worktree;
                proto.CommonDirectory = git.CommonDirectory;
                proto.Detached = git.Detached;
                proto.Ahead = git.Ahead;
                proto.Behind = git.Behind;
                proto.Staged = git.Staged;
                proto.Modified = git.Modified;
                proto.Untracked = git.Untracked;
                if (git.Branch is not null) proto.Branch = git.Branch;
                if (git.Head is not null) proto.Head = git.Head;
                if (git.Upstream is not null) proto.Upstream = git.Upstream;
                proto.ChangedFiles.AddRange(git.ChangedFiles.Order(StringComparer.Ordinal));
                break;
            default:
                throw new InvalidDataException("A child snapshot has an unknown Git inspection state.");
        }
        return proto;
    }

    private static T CheckedEnum<T>(int value) where T : struct, Enum
        => Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value)
            : throw new InvalidDataException($"A serialized value has an unknown {typeof(T).Name} value.");
}
