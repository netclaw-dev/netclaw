// -----------------------------------------------------------------------
// <copyright file="BackgroundChildRun.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

/// <summary>Framework correlation from the original admitted input and operation.</summary>
public abstract record ChildRunStartKey
{
    public required SessionId SessionId { get; init; }
    public required TurnId TurnId { get; init; }

    public sealed record Tool(ToolCallId CallId) : ChildRunStartKey;

    // A direct activation has one framework slot. The model cannot choose another slot.
    public sealed record Slash(InputId InputId) : ChildRunStartKey;
}

/// <summary>Durable acceptance facts. Runtime references and child execution state remain outside this record.</summary>
public sealed record BackgroundChildRun
{
    public required SubAgentRunId RunId { get; init; }
    public required SubAgentScopeId ScopeId { get; init; }
    public required ChildRunStartKey StartKey { get; init; }
    public required AgentName AgentName { get; init; }
    public required string SourceOperation { get; init; }
    public required string ArgumentsDigest { get; init; }
    public required TurnContextRecord OriginalContext { get; init; }
    public required IReadOnlyList<InputId> OriginInputIds { get; init; }
    public required WorkingContextSnapshot InitialWorkingSnapshot { get; init; }
    public bool StartBatchSettled { get; init; }
    public long AcceptedAtMs { get; init; }
    public long? StartedAtMs { get; init; }
    public long? CancellationRequestedAtMs { get; init; }
    public long? DispatchClosedAtMs { get; init; }
    public ChildRunCheckpoint? ChildCheckpoint { get; init; }
    public ChildRunTerminal? Terminal { get; init; }
    public long? TerminalSequenceNr { get; init; }
    public ChildRunTerminal? PreparedTerminal { get; init; }
    public InputId? DeliveryInputId { get; init; }
    public IReadOnlyList<ChildRunApproval> Approvals { get; init; } = [];

    public BackgroundChildState State => Terminal switch
    {
        { Lost: true } => BackgroundChildState.Lost,
        { Result.Completion: ChildRunCompletion.Cancelled } => BackgroundChildState.Cancelled,
        { Result.Completion: ChildRunCompletion.Completed } => BackgroundChildState.Completed,
        { Result.Completion: ChildRunCompletion.Partial } => BackgroundChildState.Partial,
        not null => BackgroundChildState.Failed,
        _ when CancellationRequestedAtMs is not null => BackgroundChildState.Cancelling,
        _ when StartedAtMs is not null => BackgroundChildState.Running,
        _ => BackgroundChildState.Accepted
    };

    public void Validate()
    {
        if (!TurnContext.TryFromRecord(OriginalContext, out var context, out var reason) || context is null)
            throw new InvalidDataException($"A child acceptance has invalid authority: {reason}");
        if (StartKey.SessionId != context.SessionId || StartKey.TurnId != context.TurnId)
            throw new InvalidDataException("A child start key differs from its original authority.");
        if (string.IsNullOrWhiteSpace(RunId.Value) || string.IsNullOrWhiteSpace(AgentName.Value)
            || ScopeId.Value != $"{StartKey.SessionId.Value}/subagent/{AgentName.Value}/{RunId.Value}")
            throw new InvalidDataException("A child acceptance has invalid owner identifiers.");
        if (ArgumentsDigest.Length != 64 || ArgumentsDigest.Any(static character => !char.IsAsciiHexDigit(character))
            || string.IsNullOrWhiteSpace(SourceOperation))
            throw new InvalidDataException("A child acceptance has invalid canonical argument evidence.");
        if (OriginInputIds.Count == 0 || OriginInputIds.Any(static id => string.IsNullOrWhiteSpace(id.Value))
            || OriginInputIds.Distinct().Count() != OriginInputIds.Count)
            throw new InvalidDataException("A child acceptance has invalid original input provenance.");
        if (DispatchClosedAtMs is not null && CancellationRequestedAtMs is null
            || PreparedTerminal is not null && Terminal is null
            || DeliveryInputId is not null && PreparedTerminal is null)
            throw new InvalidDataException("A child ledger has invalid lifecycle order.");
        if ((Terminal is null) != (TerminalSequenceNr is null) || TerminalSequenceNr is <= 0)
            throw new InvalidDataException("A child terminal has no valid journal sequence.");
        if (Terminal is { } terminal)
            ValidateTerminal(terminal);
        if (PreparedTerminal is { } prepared)
            ValidateTerminal(prepared);
        if (Terminal is not null && Approvals.Any(static approval => approval.Resolution is null))
            throw new InvalidDataException("A terminal child retains an unresolved approval prompt.");
        if (Approvals.Select(static approval => approval.Request.CallId).Distinct(StringComparer.Ordinal).Count() != Approvals.Count)
            throw new InvalidDataException("A child run has duplicate approval prompts.");
        foreach (var approval in Approvals)
            ValidateApproval(approval);
        switch (StartKey)
        {
            case ChildRunStartKey.Tool tool when !string.IsNullOrWhiteSpace(tool.CallId.Value):
                break;
            case ChildRunStartKey.Slash slash when OriginInputIds.Contains(slash.InputId) && StartBatchSettled:
                break;
            default:
                throw new InvalidDataException("A child acceptance has invalid activation correlation.");
        }
    }

    internal void ValidateApproval(ChildRunApproval approval)
    {
        var request = approval.Request;
        if (request.SourceChildRunId != RunId || request.SessionId != StartKey.SessionId
            || request.TurnContext is null || !SessionState.SameCanonicalContext(OriginalContext, request.TurnContext)
            || request.OriginalChildCallId is not { } childCall || string.IsNullOrWhiteSpace(childCall.Value)
            || !request.CallId.StartsWith($"{RunId.Value}/subagent-approval/", StringComparison.Ordinal)
            || !AuthorizationAttemptId.TryParse(request.AuthorizationAttemptId, out _)
            || request.Audience != OriginalContext.Audience || request.Boundary != OriginalContext.Boundary
            || request.ChannelType != OriginalContext.ChannelType
            || request.RequesterSenderId != OriginalContext.RequesterSenderId
            || request.SupportsInteractiveApproval != OriginalContext.SupportsInteractiveApproval
            || request.RequesterPrincipal != OriginalContext.RequesterPrincipal
            || request.HasThirdPartyAdoptedContext != OriginalContext.HasThirdPartyAdoptedContext
            || !request.AdoptedSpeakerIds.SequenceEqual(OriginalContext.AdoptedSpeakerIds, StringComparer.Ordinal)
            || request.OptionKeys.Count == 0 || string.IsNullOrWhiteSpace(request.ToolName))
            throw new InvalidDataException("A child approval differs from its accepted authority or call.");
        if (approval.Resolution is { } resolution
            && (resolution.SourceChildRunId != RunId || resolution.SessionId != request.SessionId
                || resolution.CallId != request.CallId || resolution.AuthorizationAttemptId != request.AuthorizationAttemptId))
            throw new InvalidDataException("A child approval resolution differs from its request.");
    }

    private void ValidateTerminal(ChildRunTerminal terminal)
    {
        if (terminal.Result.RunId != RunId || terminal.Result.ScopeId != ScopeId
            || terminal.Result.AgentName != AgentName
            || terminal.Lost && terminal.Result.Completion is not ChildRunCompletion.Failed
            || CancellationRequestedAtMs is not null && terminal.Result.Completion is not ChildRunCompletion.Cancelled)
            throw new InvalidDataException("A child terminal differs from its accepted run or durable cancellation.");
    }
}

public enum BackgroundChildState
{
    Accepted, Running, Cancelling, Completed, Partial, Failed, Cancelled, Lost
}

/// <summary>Evidence from a completed child tool round. It grants no authority.</summary>
public sealed record ChildRunCheckpoint(int CompletedRound, string Summary, WorkingContextDelta ConfirmedActivity);

/// <summary>A recorded terminal result. Enrichment cannot replace its outcome.</summary>
public sealed record ChildRunTerminal(SubAgentResult Result, bool Lost, string? EvidenceWarning);

/// <summary>A run owns its approval request after the parent turn ends.</summary>
public sealed record ChildRunApproval(ToolApprovalRequested Request, ToolApprovalResolved? Resolution);
