// -----------------------------------------------------------------------
// <copyright file="NetclawProtoMapper.ChildRuns.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Proto = Netclaw.Actors.Serialization.Proto;
using GitInspectionKind = Netclaw.Actors.Serialization.Proto.GitWorkingContextInspectionProto.Types.Kind;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Serialization;

internal static partial class NetclawProtoMapper
{
    internal static Proto.ChildRunAcceptedProto ToProto(ChildRunAccepted evt) => new()
    {
        SessionId = evt.SessionId.Value,
        Run = ToProto(evt.Run)
    };

    internal static ChildRunAccepted FromProto(Proto.ChildRunAcceptedProto proto) => new()
    {
        SessionId = new SessionId(proto.SessionId),
        Run = FromProto(proto.Run ?? throw new InvalidDataException("A child acceptance has no run record."))
    };

    internal static Proto.BackgroundChildRunProto ToProto(BackgroundChildRun run)
    {
        run.Validate();
        var proto = new Proto.BackgroundChildRunProto
        {
            RunId = run.RunId.Value, ScopeId = run.ScopeId.Value,
            OwnerSessionId = run.StartKey.SessionId.Value, OriginalTurnId = run.StartKey.TurnId.Value,
            AgentName = run.AgentName.Value, SourceOperation = run.SourceOperation, ArgumentsDigest = run.ArgumentsDigest,
            OriginalContext = ToProto(run.OriginalContext), InitialWorkingContext = ToProto(run.InitialWorkingSnapshot.WorkingContext),
            InitialGit = ToProto(run.InitialWorkingSnapshot.Git),
            StartBatchSettled = run.StartBatchSettled, AcceptedAtMs = run.AcceptedAtMs
        };
        if (run.StartedAtMs is { } started) proto.StartedAtMs = started;
        if (run.CancellationRequestedAtMs is { } cancelled) proto.CancellationRequestedAtMs = cancelled;
        if (run.DispatchClosedAtMs is { } closed) proto.DispatchClosedAtMs = closed;
        if (run.ChildCheckpoint is { } checkpoint) proto.ChildCheckpoint = ToProto(checkpoint);
        if (run.Terminal is { } terminal) proto.Terminal = ToProto(terminal);
        if (run.TerminalSequenceNr is { } sequence) proto.TerminalSequenceNr = sequence;
        if (run.PreparedTerminal is { } prepared) proto.PreparedTerminal = ToProto(prepared);
        if (run.DeliveryInputId is { } delivery) proto.DeliveryInputId = delivery.Value;
        switch (run.StartKey)
        {
            case ChildRunStartKey.Tool tool:
                proto.ToolCallId = tool.CallId.Value;
                break;
            case ChildRunStartKey.Slash slash:
                proto.SlashInputId = slash.InputId.Value;
                break;
            default:
                throw new InvalidDataException("A child acceptance has an unknown activation kind.");
        }
        proto.Approvals.AddRange(run.Approvals.Select(static approval => new Proto.ChildRunApprovalProto
        {
            Request = ToProto(approval.Request),
            Resolution = approval.Resolution is null ? null : ToProto(approval.Resolution)
        }));
        proto.OriginInputIds.AddRange(run.OriginInputIds.Select(static id => id.Value));
        return proto;
    }

    internal static BackgroundChildRun FromProto(Proto.BackgroundChildRunProto proto)
    {
        var session = new SessionId(proto.OwnerSessionId);
        var turn = new TurnId(proto.OriginalTurnId);
        ChildRunStartKey key = proto.ActivationCase switch
        {
            Proto.BackgroundChildRunProto.ActivationOneofCase.ToolCallId => new ChildRunStartKey.Tool(new ToolCallId(proto.ToolCallId)) { SessionId = session, TurnId = turn },
            Proto.BackgroundChildRunProto.ActivationOneofCase.SlashInputId => new ChildRunStartKey.Slash(new InputId(proto.SlashInputId)) { SessionId = session, TurnId = turn },
            _ => throw new InvalidDataException("A child acceptance has no activation identity.")
        };
        if (proto.OriginalContext is null || proto.InitialWorkingContext is null || proto.InitialGit is null)
            throw new InvalidDataException("A child acceptance lacks mandatory canonical evidence.");
        var run = new BackgroundChildRun
        {
            RunId = new SubAgentRunId(proto.RunId), ScopeId = new SubAgentScopeId(proto.ScopeId), StartKey = key,
            AgentName = new AgentName(proto.AgentName), SourceOperation = proto.SourceOperation, ArgumentsDigest = proto.ArgumentsDigest,
            OriginalContext = FromProto(proto.OriginalContext), OriginInputIds = proto.OriginInputIds.Select(static id => new InputId(id)).ToArray(),
            // The host shell object stays live-only. Recovery never reconstructs child execution.
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = FromProto(proto.InitialWorkingContext), Git = FromProto(proto.InitialGit)
            },
            StartBatchSettled = proto.StartBatchSettled, AcceptedAtMs = proto.AcceptedAtMs,
            StartedAtMs = proto.HasStartedAtMs ? proto.StartedAtMs : null,
            CancellationRequestedAtMs = proto.HasCancellationRequestedAtMs ? proto.CancellationRequestedAtMs : null,
            DispatchClosedAtMs = proto.HasDispatchClosedAtMs ? proto.DispatchClosedAtMs : null,
            ChildCheckpoint = proto.ChildCheckpoint is null ? null : FromProto(proto.ChildCheckpoint),
            Terminal = proto.Terminal is null ? null : FromProto(proto.Terminal),
            TerminalSequenceNr = proto.HasTerminalSequenceNr ? proto.TerminalSequenceNr : null,
            PreparedTerminal = proto.PreparedTerminal is null ? null : FromProto(proto.PreparedTerminal),
            DeliveryInputId = proto.HasDeliveryInputId ? new InputId(proto.DeliveryInputId) : null,
            Approvals = proto.Approvals.Select(static approval => new ChildRunApproval(
                approval.Request is null ? throw new InvalidDataException("A child approval has no request.") : FromProto(approval.Request),
                approval.Resolution is null ? null : FromProto(approval.Resolution))).ToArray()
        };
        run.Validate();
        return run;
    }

    private static Proto.GitWorkingContextInspectionProto ToProto(GitWorkingContextInspection inspection)
    {
        var proto = new Proto.GitWorkingContextInspectionProto();
        switch (inspection)
        {
            case GitWorkingContextInspection.Skipped:
                proto.Kind = GitInspectionKind.Skipped;
                break;
            case GitWorkingContextInspection.NotRepository:
                proto.Kind = GitInspectionKind.NotRepository;
                break;
            case GitWorkingContextInspection.ExecutableNotFound:
                proto.Kind = GitInspectionKind.ExecutableNotFound;
                break;
            case GitWorkingContextInspection.Unavailable unavailable:
                proto.Kind = GitInspectionKind.Unavailable;
                proto.Reason = unavailable.Reason;
                break;
            case GitWorkingContextInspection.Available { Snapshot: var git }:
                _ = git.GetHeadState();
                proto.Kind = GitInspectionKind.Available;
                proto.Worktree = git.Worktree; proto.CommonDirectory = git.CommonDirectory;
                proto.Detached = git.Detached; proto.Ahead = git.Ahead; proto.Behind = git.Behind;
                proto.Staged = git.Staged; proto.Modified = git.Modified; proto.Untracked = git.Untracked;
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

    private static GitWorkingContextInspection FromProto(Proto.GitWorkingContextInspectionProto proto)
    {
        if (proto.Kind != GitInspectionKind.Available)
            return proto.Kind switch
            {
                GitInspectionKind.Skipped => new GitWorkingContextInspection.Skipped(),
                GitInspectionKind.NotRepository => new GitWorkingContextInspection.NotRepository(),
                GitInspectionKind.ExecutableNotFound => new GitWorkingContextInspection.ExecutableNotFound(),
                GitInspectionKind.Unavailable when proto.HasReason => new GitWorkingContextInspection.Unavailable(proto.Reason),
                _ => throw new InvalidDataException("A child snapshot has invalid Git inspection evidence.")
            };
        var git = new GitWorkingContextSnapshot
        {
            Worktree = proto.Worktree, CommonDirectory = proto.CommonDirectory,
            Branch = proto.HasBranch ? proto.Branch : null, Detached = proto.Detached,
            Head = proto.HasHead ? proto.Head : null, Upstream = proto.HasUpstream ? proto.Upstream : null,
            Ahead = proto.Ahead, Behind = proto.Behind, Staged = proto.Staged, Modified = proto.Modified, Untracked = proto.Untracked,
            ChangedFiles = proto.ChangedFiles.ToImmutableHashSet(StringComparer.Ordinal)
        };
        _ = git.GetHeadState();
        return new GitWorkingContextInspection.Available(git);
    }
}
