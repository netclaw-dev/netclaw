// -----------------------------------------------------------------------
// <copyright file="NetclawProtoMapper.CoordinationV2.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Google.Protobuf;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Proto = Netclaw.Actors.Serialization.Proto;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Serialization;

internal static partial class NetclawProtoMapper
{
    private static Proto.ChildContinuationProto ToProto(ChildContinuation continuation) => new()
    {
        RunId = continuation.RunId.Value,
        DeliveryCallId = continuation.DeliveryCallId.Value
    };

    private static ChildContinuation FromProto(Proto.ChildContinuationProto proto) => new(
        new SubAgentRunId(proto.RunId), new ToolCallId(proto.DeliveryCallId));

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
        var snapshot = FromProto(proto);
        var legacyRuns = proto.ChildRuns.Select(run => FromLegacyChildRun(run)).ToArray();
        var evidence = new List<LegacyCoordinationEvidence>(snapshot.LegacyCoordinationEvidence);
        if (proto.AdoptedTaskContext is not null || proto.AdoptedTaskInputIds.Count > 0)
        {
            evidence.Add(new LegacyCoordinationEvidence
            {
                Manifest = "ss-v1-adopted-task",
                Payload = [],
                Disposition = LegacyCoordinationDisposition.PreservedLegacyFacts,
                AdoptedContext = proto.AdoptedTaskContext is null
                    ? null : FromLegacyTurnContext(proto.AdoptedTaskContext),
                InputIds = proto.AdoptedTaskInputIds.Select(static id => new InputId(id)).ToArray(),
                ActiveInputIds = proto.AdoptedTaskInputIds
                    .Where(id => snapshot.PendingInputs.Any(input => input.InputId.Value == id))
                    .Select(static id => new InputId(id)).ToArray(),
                Reason = "The deployed snapshot has adopted-task context that applies to its listed pending inputs."
            });
        }

        var rawSnapshotRetained = false;
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
                && TryGetLegacySnapshotDelivery(snapshot, proto, oldRun, run, out var delivery, out var pending))
            {
                var activeParentTask = pending || proto.AdoptedTaskInputIds.Contains(oldRun.DeliveryInputId);
                legacyRuns[index] = run with
                {
                    InitialWorkingSnapshot = null,
                    Lifecycle = pending
                        ? new ChildRunLifecycle.Prepared(delivery)
                        : activeParentTask
                            ? new ChildRunLifecycle.Consumed(delivery)
                            : new ChildRunLifecycle.Closed(delivery.Result.ToolCallId,
                                delivery.Terminal.Result.Outcome, delivery.Terminal.Result.OutcomeReason)
                };
                if (!activeParentTask)
                    continue;
                evidence.Add(new LegacyCoordinationEvidence
                {
                    Manifest = "ss-v1",
                    Payload = [],
                    Disposition = LegacyCoordinationDisposition.AppliedChildContinuation,
                    InputIds = inputIds,
                    ActiveInputIds = pending ? inputIds : Array.Empty<InputId>(),
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

        return snapshot with
        {
            ChildRuns = legacyRuns,
            LegacyCoordinationEvidence = evidence
        };
    }

    private static bool TryGetLegacySnapshotDelivery(SessionSnapshot snapshot,
        Proto.SessionSnapshotProto snapshotProto, Proto.BackgroundChildRunProto oldRun,
        BackgroundChildRun run, out ChildPreparedResult delivery, out bool pending)
    {
        delivery = null!;
        pending = false;
        if (run.Lifecycle is not ChildRunLifecycle.Terminal terminal)
            return false;
        var toolName = LegacyCoordinationReplay.LegacyToolName(run);
        var expectedBody = LegacyCoordinationReplay.LegacyTerminalBody(run, terminal.Result,
            oldRun.ChildCheckpoint is null ? null : LegacyCoordinationReplay.FromLegacyCheckpoint(oldRun.ChildCheckpoint));
        var pendingProto = snapshotProto.PendingInputs.SingleOrDefault(input => input.InputId == oldRun.DeliveryInputId);
        if (pendingProto is not null)
        {
            if (!pendingProto.HasSourceChildRunId || pendingProto.SourceChildRunId != run.RunId.Value
                || pendingProto.UserMessage?.ToolCallId is not { } pendingCallId
                || run.StartKey is ChildRunStartKey.Tool original && original.CallId.Value == pendingCallId)
                return false;
            var result = FromProto(pendingProto.UserMessage);
            var context = pendingProto.TurnContext is null ? null : FromLegacyTurnContext(pendingProto.TurnContext);
            if (pendingProto.SessionId?.Value != run.StartKey.SessionId.Value
                || context is null || !LegacyCoordinationReplay.SameContext(context, run.OriginalContext)
                || result.Role != ChatRole.Tool || result.Name != toolName || result.Content != expectedBody)
                return false;
            var callId = new ToolCallId(pendingCallId);
            var call = LegacyCoordinationReplay.LegacyDeliveryCall(run, callId);
            delivery = new ChildPreparedResult(terminal.Result, call, result, null);
            pending = true;
            return true;
        }

        var expectedArguments = LegacyCoordinationReplay.LegacyDeliveryArguments(run);
        var callIds = snapshot.History.SelectMany(message => message.ToolCalls)
            .Where(call => call.Name.Value == toolName && call.ArgumentsJson == expectedArguments)
            .Select(static call => call.CallId).Distinct().ToArray();
        if (callIds.Length != 1)
            return false;
        var matchingResults = snapshot.History.Where(message => message.Role == ChatRole.Tool
            && message.ToolCallId == callIds[0] && message.Name == toolName && message.Content == expectedBody).ToArray();
        if (matchingResults.Length != 1)
            return false;
        var canonicalCall = LegacyCoordinationReplay.LegacyDeliveryCall(run, callIds[0]);
        if (!snapshot.History.Any(message => message.ToolCalls.Count(call => call.CallId == callIds[0]) == 1))
            return false;
        delivery = new ChildPreparedResult(terminal.Result, canonicalCall, matchingResults[0], null);
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
            Lifecycle = lifecycle
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
            case ChildRunEvent.ResultPrepared prepared:
                proto.ResultPrepared = new Proto.ChildPreparedResultV2Proto
                {
                    Call = ToProto(prepared.Call),
                    Result = ToProto(prepared.Result)
                };
                if (prepared.ReviewedWorkingContext is not null)
                    proto.ResultPrepared.ReviewedWorkingContext = LegacyCoordinationReplay.ToLegacyWorkingDelta(prepared.ReviewedWorkingContext);
                break;
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
            _ => throw new InvalidDataException("A v2 child event has no valid fact.")
        };
    }

    private static Proto.BackgroundChildRunV2Proto ToProto(BackgroundChildRun run)
    {
        var needsInitialSnapshot = run.Lifecycle is ChildRunLifecycle.Accepted
            or ChildRunLifecycle.Cancelling or ChildRunLifecycle.Terminal;
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
            AcceptedAtMs = run.AcceptedAtMs
        };
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
            case ChildRunLifecycle.Terminal terminal:
                proto.Terminal = LegacyCoordinationReplay.ToLegacyTerminal(terminal.Result);
                break;
            case ChildRunLifecycle.Prepared prepared:
                proto.Prepared = ToProto(prepared.Delivery);
                break;
            case ChildRunLifecycle.Consumed consumed:
                proto.Consumed = ToProto(consumed.Delivery);
                break;
            case ChildRunLifecycle.Closed closed:
                proto.Closed = new Proto.ChildRunClosedV2Proto
                {
                    DeliveryCallId = closed.DeliveryCallId.Value,
                    Outcome = (int)closed.Outcome
                };
                if (closed.Reason is not null)
                    proto.Closed.Reason = closed.Reason.Value.Value;
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
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Prepared =>
                new ChildRunLifecycle.Prepared(FromProto(proto.Prepared)),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Consumed =>
                new ChildRunLifecycle.Consumed(FromProto(proto.Consumed)),
            Proto.BackgroundChildRunV2Proto.LifecycleOneofCase.Closed =>
                new ChildRunLifecycle.Closed(new ToolCallId(proto.Closed.DeliveryCallId),
                    CheckedEnum<SubAgentRunOutcome>(proto.Closed.Outcome),
                    proto.Closed.HasReason ? new SubAgentOutcomeReason(proto.Closed.Reason) : null),
            _ => throw new InvalidDataException("A v2 child snapshot has no lifecycle.")
        };
        var needsInitialSnapshot = lifecycle is ChildRunLifecycle.Accepted
            or ChildRunLifecycle.Cancelling or ChildRunLifecycle.Terminal;
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
            Lifecycle = lifecycle
        };
    }

    private static Proto.ChildPreparedStateV2Proto ToProto(ChildPreparedResult delivery) => new()
    {
        Terminal = LegacyCoordinationReplay.ToLegacyTerminal(delivery.Terminal),
        Call = ToProto(delivery.Call),
        Result = ToProto(delivery.Result),
        ReviewedWorkingContext = delivery.ReviewedWorkingContext is null ? null
            : LegacyCoordinationReplay.ToLegacyWorkingDelta(delivery.ReviewedWorkingContext)
    };

    private static ChildPreparedResult FromProto(Proto.ChildPreparedStateV2Proto proto)
    {
        if (proto.Terminal is null || proto.Call is null || proto.Result is null)
            throw new InvalidDataException("A v2 prepared child result lacks its exact pair.");
        return new ChildPreparedResult(FromLegacyTerminal(proto.Terminal),
            FromProto(proto.Call), FromProto(proto.Result), proto.ReviewedWorkingContext is null ? null
                : LegacyCoordinationReplay.FromLegacyWorkingDelta(proto.ReviewedWorkingContext));
    }

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
