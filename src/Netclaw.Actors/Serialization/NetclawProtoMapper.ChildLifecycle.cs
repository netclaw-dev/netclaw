// -----------------------------------------------------------------------
// <copyright file="NetclawProtoMapper.ChildLifecycle.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Proto = Netclaw.Actors.Serialization.Proto;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Serialization;

internal static partial class NetclawProtoMapper
{
    internal static Proto.ChildRunEventProto ToProto(ChildRunEvent evt)
    {
        var proto = new Proto.ChildRunEventProto
        {
            SessionId = evt.SessionId.Value, RunId = evt.RunId.Value, RecordedAtMs = evt.RecordedAtMs
        };
        switch (evt)
        {
            case ChildRunEvent.Started: proto.Started = true; break;
            case ChildRunEvent.Checkpointed checkpoint: proto.Checkpoint = ToProto(checkpoint.Checkpoint); break;
            case ChildRunEvent.CancellationRequested: proto.CancellationRequested = true; break;
            case ChildRunEvent.DispatchClosed: proto.DispatchClosed = true; break;
            case ChildRunEvent.TerminalRecorded terminal when terminal.TerminalSequenceNr > 0:
                proto.Terminal = ToProto(terminal.Terminal); proto.TerminalSequenceNr = terminal.TerminalSequenceNr; break;
            case ChildRunEvent.ResultPrepared prepared when prepared.TerminalSequenceNr > 0:
                proto.Prepared = ToProto(prepared.Terminal); proto.TerminalSequenceNr = prepared.TerminalSequenceNr; break;
            case ChildRunEvent.DeliveryAdmitted delivery: proto.Delivery = ToProto(delivery.Input); break;
            default: throw new InvalidDataException("A child lifecycle event has an unknown fact.");
        }
        return proto;
    }

    internal static ChildRunEvent FromProto(Proto.ChildRunEventProto proto)
    {
        var sessionId = new SessionId(proto.SessionId);
        var runId = new SubAgentRunId(proto.RunId);
        return proto.FactCase switch
        {
            Proto.ChildRunEventProto.FactOneofCase.Started when proto.Started => new ChildRunEvent.Started
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            Proto.ChildRunEventProto.FactOneofCase.Checkpoint => new ChildRunEvent.Checkpointed(FromProto(proto.Checkpoint))
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            Proto.ChildRunEventProto.FactOneofCase.CancellationRequested when proto.CancellationRequested => new ChildRunEvent.CancellationRequested
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            Proto.ChildRunEventProto.FactOneofCase.DispatchClosed when proto.DispatchClosed => new ChildRunEvent.DispatchClosed
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            Proto.ChildRunEventProto.FactOneofCase.Terminal when proto.TerminalSequenceNr > 0 => new ChildRunEvent.TerminalRecorded(FromProto(proto.Terminal), proto.TerminalSequenceNr)
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            Proto.ChildRunEventProto.FactOneofCase.Prepared when proto.TerminalSequenceNr > 0 => new ChildRunEvent.ResultPrepared(FromProto(proto.Prepared), proto.TerminalSequenceNr)
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            Proto.ChildRunEventProto.FactOneofCase.Delivery => new ChildRunEvent.DeliveryAdmitted(FromProto(proto.Delivery))
                { SessionId = sessionId, RunId = runId, RecordedAtMs = proto.RecordedAtMs },
            _ => throw new InvalidDataException("A child lifecycle event has no valid fact.")
        };
    }

    private static Proto.ChildWorkingContextProto ToProto(WorkingContextDelta delta)
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

    private static WorkingContextDelta FromProto(Proto.ChildWorkingContextProto proto) => new()
    {
        ProjectDirectory = proto.HasProjectDirectory ? proto.ProjectDirectory : null,
        Worktree = proto.HasWorktree ? proto.Worktree : null,
        Branch = proto.HasBranch ? proto.Branch : null,
        Head = proto.HasHead ? proto.Head : null,
        ReadFiles = proto.ReadFiles.ToArray(), ConfirmedChangedFiles = proto.ConfirmedChangedFiles.ToArray(),
        ObservedChangedFiles = proto.ObservedChangedFiles.ToArray()
    };

    private static Proto.ChildRunCheckpointProto ToProto(ChildRunCheckpoint checkpoint) => new()
    {
        CompletedRound = checkpoint.CompletedRound, Summary = checkpoint.Summary, ConfirmedActivity = ToProto(checkpoint.ConfirmedActivity)
    };

    private static ChildRunCheckpoint FromProto(Proto.ChildRunCheckpointProto proto)
        => new(proto.CompletedRound, proto.Summary, FromProto(proto.ConfirmedActivity
            ?? throw new InvalidDataException("A child checkpoint lacks confirmed activity evidence.")));

    private static Proto.ChildRunTerminalProto ToProto(ChildRunTerminal terminal)
    {
        var result = terminal.Result;
        var proto = new Proto.ChildRunTerminalProto
        {
            Lost = terminal.Lost, Output = result.Output, Outcome = (int)result.Outcome,
            RunId = result.RunId?.Value ?? throw new InvalidDataException("A child terminal has no run ID."),
            ScopeId = result.ScopeId?.Value ?? throw new InvalidDataException("A child terminal has no scope ID."),
            AgentName = result.AgentName.Value, FindingsCount = result.FindingsCount
        };
        if (terminal.EvidenceWarning is not null) proto.EvidenceWarning = terminal.EvidenceWarning;
        if (result.OutcomeReason is { } reason) proto.OutcomeReason = reason.Value;
        if (result.WorkingContext is { } delta) proto.WorkingContext = ToProto(delta);
        if (result.LogPath is not null) proto.LogPath = result.LogPath;
        if (result.ArtifactDirectory is not null) proto.ArtifactDirectory = result.ArtifactDirectory;
        foreach (var finding in result.Findings)
        {
            var item = new Proto.ChildFindingProto
            {
                Shape = (int)finding.Shape, Title = finding.Title, Content = finding.Content, Kind = finding.Kind,
                Sensitivity = (int)finding.Sensitivity, RecallMode = (int)finding.RecallMode,
                UpdateSemantics = finding.UpdateSemantics, Confidence = finding.Confidence,
                Durability = (int)finding.Durability, Reusability = (int)finding.Reusability
            };
            item.Evidence.AddRange(finding.Evidence);
            if (finding.FreshnessAtMs is { } fresh) item.FreshnessAtMs = fresh;
            proto.Findings.Add(item);
        }
        return proto;
    }

    private static ChildRunTerminal FromProto(Proto.ChildRunTerminalProto proto)
    {
        var outcome = (SubAgentRunOutcome)proto.Outcome;
        if (!Enum.IsDefined(outcome)) throw new InvalidDataException("A child terminal has an unknown outcome.");
        var result = new SubAgentResult
        {
            Completion = ChildRunCompletion.FromReportedOutcome(outcome,
                proto.HasOutcomeReason ? new SubAgentOutcomeReason(proto.OutcomeReason) : null,
                proto.WorkingContext is null ? null : FromProto(proto.WorkingContext)),
            Output = proto.Output, AgentName = new AgentName(proto.AgentName),
            RunId = new SubAgentRunId(proto.RunId), ScopeId = new SubAgentScopeId(proto.ScopeId),
            FindingsCount = proto.FindingsCount,
            LogPath = proto.HasLogPath ? proto.LogPath : null,
            ArtifactDirectory = proto.HasArtifactDirectory ? proto.ArtifactDirectory : null,
            Findings = proto.Findings.Select(static item => new SubAgentFinding
            {
                Shape = CheckedEnum<SubAgentFindingShape>(item.Shape), Title = item.Title, Content = item.Content,
                Kind = item.Kind, Sensitivity = CheckedEnum<SubAgentFindingSensitivity>(item.Sensitivity),
                RecallMode = CheckedEnum<SubAgentFindingRecallMode>(item.RecallMode), UpdateSemantics = item.UpdateSemantics,
                Confidence = item.Confidence, Durability = CheckedEnum<SubAgentFindingDurability>(item.Durability),
                Reusability = CheckedEnum<SubAgentFindingReusability>(item.Reusability), Evidence = item.Evidence.ToArray(),
                FreshnessAtMs = item.HasFreshnessAtMs ? item.FreshnessAtMs : null
            }).ToList()
        };
        return new ChildRunTerminal(result, proto.Lost, proto.HasEvidenceWarning ? proto.EvidenceWarning : null);
    }

    private static T CheckedEnum<T>(int value) where T : struct, Enum
        => Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value)
            : throw new InvalidDataException($"A child finding has an unknown {typeof(T).Name} value.");
}
