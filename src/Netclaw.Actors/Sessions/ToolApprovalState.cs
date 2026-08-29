// -----------------------------------------------------------------------
// <copyright file="ToolApprovalState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal sealed record PendingToolInteraction(
    ToolApprovalRequested Request,
    AuthorizationAttemptId AuthorizationAttemptId,
    bool PersistApprovalState,
    TurnContext? TurnContext,
    string? TurnContextRestoreFailure) : INoSerializationVerificationNeeded
{
    public static PendingToolInteraction From(
        ToolApprovalRequested evt,
        bool persistApprovalState)
    {
        var turnContext = ToolApprovalTurnContext.Restore(evt, out var restoreFailure);
        var authorizationAttemptId = ToolApprovalTurnContext.RestoreAuthorizationAttemptId(evt);
        return new PendingToolInteraction(
            evt,
            authorizationAttemptId,
            persistApprovalState,
            turnContext,
            restoreFailure);
    }
}

// Internal actor message for approval prompts. The request is the public output
// shape; PersistApprovalState is session routing policy that decides whether the
// prompt becomes durable parent-session approval state.
internal sealed record ToolInteractionRequestDispatch(
    SessionProtocol.ToolInteractionRequest Request,
    bool PersistApprovalState) : INoSerializationVerificationNeeded
{
    internal string? SessionScratchDirectory { get; init; }
}

internal enum ApprovalTurnPhase
{
    None,
    Running,
    Waiting,
    RecoveredWaiting,
    Redriving,
    Abandoning
}

internal sealed record ApprovalRedrivePlan(
    IReadOnlyDictionary<string, IReadOnlyList<string>>? OneTimeApprovalPreSeed,
    IReadOnlyDictionary<string, ApprovalDecision>? DecisionOverride,
    IReadOnlyDictionary<string, string>? SessionScratchDenialDirectories,
    IReadOnlyDictionary<string, AuthorizationAttemptId>? AuthorizationAttemptIds);

internal abstract record ToolApprovalCallState(PendingToolInteraction Pending);

internal sealed record PendingToolApproval(PendingToolInteraction Pending)
    : ToolApprovalCallState(Pending);

internal sealed record ResolvedToolApproval(
    PendingToolInteraction Pending,
    ApprovalDecision Decision) : ToolApprovalCallState(Pending);

/// <summary>
/// Owns all actor-local approval state for one session.
/// Durable events remain the source of truth after recovery.
/// </summary>
internal sealed class ToolApprovalState
{
    private readonly Dictionary<string, ToolApprovalCallState> _calls = new(StringComparer.Ordinal);

    public int PendingCount => _calls.Values.Count(static call => call is PendingToolApproval);

    public int ResolvedCount => _calls.Values.Count(static call => call is ResolvedToolApproval);

    public ApprovalTurnPhase TurnPhase { get; private set; }

    public TurnContext? TurnContext { get; private set; }

    public PendingToolInteraction Request(
        ToolApprovalRequested evt,
        bool persistApprovalState,
        bool recovered)
    {
        var pending = PendingToolInteraction.From(evt, persistApprovalState);
        _calls[evt.CallId] = new PendingToolApproval(pending);

        if (persistApprovalState && pending.TurnContext is { } context)
        {
            TurnContext = context;
            TurnPhase = recovered
                ? ApprovalTurnPhase.RecoveredWaiting
                : ApprovalTurnPhase.Waiting;
        }

        return pending;
    }

    public bool TryGetPending(string callId, out PendingToolInteraction pending)
    {
        if (_calls.TryGetValue(callId, out var call)
            && call is PendingToolApproval pendingCall)
        {
            pending = pendingCall.Pending;
            return true;
        }

        pending = null!;
        return false;
    }

    public bool TryGetResolved(string callId, out ResolvedToolApproval resolved)
    {
        if (_calls.TryGetValue(callId, out var call)
            && call is ResolvedToolApproval resolvedCall)
        {
            resolved = resolvedCall;
            return true;
        }

        resolved = null!;
        return false;
    }

    public bool HasPending(string callId)
        => _calls.TryGetValue(callId, out var call) && call is PendingToolApproval;

    public bool HasResolved(string callId)
        => _calls.TryGetValue(callId, out var call) && call is ResolvedToolApproval;

    public bool RemovePending(string callId)
        => _calls.TryGetValue(callId, out var call)
           && call is PendingToolApproval
           && _calls.Remove(callId);

    public bool Resolve(
        string callId,
        ApprovalDecision decision,
        out PendingToolInteraction pending)
    {
        if (!TryGetPending(callId, out pending))
            return false;

        _calls[callId] = new ResolvedToolApproval(pending, decision);
        if (PendingCount == 0 && pending.TurnContext is { } context)
        {
            TurnContext = context;
            TurnPhase = ApprovalTurnPhase.Running;
        }

        return true;
    }

    public PendingToolInteraction? FindLatestPending(
        Func<PendingToolInteraction, bool> predicate)
        => _calls.Values
            .OfType<PendingToolApproval>()
            .Select(static call => call.Pending)
            .Where(predicate)
            .OrderBy(static pending => pending.Request.RequestedAtMs)
            .LastOrDefault();

    public void StartTurn(TurnContext context)
    {
        TurnContext = context;
        TurnPhase = ApprovalTurnPhase.Running;
    }

    public bool MarkAbandoning()
    {
        if (TurnPhase is not ApprovalTurnPhase.Waiting
            and not ApprovalTurnPhase.RecoveredWaiting)
        {
            return false;
        }

        TurnPhase = ApprovalTurnPhase.Abandoning;
        return true;
    }

    public bool MarkRedriving(PendingToolInteraction pending)
    {
        if (TurnPhase is not ApprovalTurnPhase.Running
            || pending.TurnContext is not { } context)
        {
            return false;
        }

        TurnContext = context;
        TurnPhase = ApprovalTurnPhase.Redriving;
        return true;
    }

    public void MarkRunningAfterRedrive()
    {
        if (TurnPhase is not ApprovalTurnPhase.Redriving)
            return;

        TurnPhase = ApprovalTurnPhase.Running;
    }

    public ApprovalRedrivePlan BuildRedrivePlan(IEnumerable<string> callIds)
    {
        var builder = new ApprovalRedrivePlanBuilder();

        foreach (var callId in callIds)
        {
            if (TryGetResolved(callId, out var resolved))
                builder.Add(callId, resolved);
        }

        return builder.Build();
    }

    public void ClearCalls() => _calls.Clear();

    public void ClearTurn()
    {
        TurnContext = null;
        TurnPhase = ApprovalTurnPhase.None;
    }

    private sealed class ApprovalRedrivePlanBuilder
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _preSeed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ApprovalDecision> _decisionOverride = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessionScratchDenialDirectories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AuthorizationAttemptId> _authorizationAttemptIds = new(StringComparer.Ordinal);

        public void Add(string callId, ResolvedToolApproval resolved)
        {
            var request = resolved.Pending.Request;
            _authorizationAttemptIds[callId] = resolved.Pending.AuthorizationAttemptId;

            if (resolved.Decision.IsApprovalGrant())
            {
                _preSeed[callId] = OneTimeApprovalKeys.Create(
                    request.Patterns,
                    request.Candidates,
                    request.Cwd);
            }

            if (resolved.Decision is not (ApprovalDecision.Denied or ApprovalDecision.TimedOut))
                return;

            _decisionOverride[callId] = resolved.Decision;
            if (resolved.Decision == ApprovalDecision.Denied
                && request.SessionScratchDirectory is { Length: > 0 } scratchDirectory)
            {
                _sessionScratchDenialDirectories[callId] = scratchDirectory;
            }
        }

        public ApprovalRedrivePlan Build()
            => new(
                _preSeed.Count == 0 ? null : _preSeed,
                _decisionOverride.Count == 0 ? null : _decisionOverride,
                _sessionScratchDenialDirectories.Count == 0 ? null : _sessionScratchDenialDirectories,
                _authorizationAttemptIds.Count == 0 ? null : _authorizationAttemptIds);
    }
}

internal static class ToolApprovalTurnContext
{
    public static AuthorizationAttemptId RestoreAuthorizationAttemptId(ToolApprovalRequested evt)
        => AuthorizationAttemptId.TryParse(evt.AuthorizationAttemptId, out var restoredAttemptId)
            ? restoredAttemptId
            : AuthorizationAttemptId.New();

    public static TurnContext? Restore(ToolApprovalRequested evt, out string? failure)
    {
        if (evt.TurnContext is not null)
        {
            if (TurnContext.TryFromRecord(evt.TurnContext, out var context, out failure))
                return context;

            return null;
        }

        return RestoreLegacy(evt, out failure);
    }

    private static TurnContext? RestoreLegacy(ToolApprovalRequested evt, out string? failure)
    {
        failure = null;

        if (!ChannelTypeExtensions.TryFromWireValue(evt.ChannelType, out var channelType))
        {
            failure = "legacy approval event is missing channel type";
            return null;
        }

        var requesterPrincipal = evt.RequesterPrincipal ?? PrincipalClassification.UntrustedExternal;
        if (requesterPrincipal is not PrincipalClassification.VerifiedAutomation
            && evt.RequesterSenderId is null)
        {
            failure = "legacy approval event is missing requester sender";
            return null;
        }

        return new TurnContext
        {
            SessionId = evt.SessionId,
            TurnId = new TurnId($"recovered-approval/{evt.CallId}"),
            Audience = evt.Audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundary(evt.Boundary, evt.ChannelType, evt.Audience),
            ChannelType = channelType,
            RequesterSenderId = evt.RequesterSenderId,
            RequesterPrincipal = requesterPrincipal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Unknown)
            {
                SourceKind = new SourceKind(channelType.ToWireValue())
            },
            HasAdoptedContext = evt.HasThirdPartyAdoptedContext || evt.AdoptedSpeakerIds.Count > 0,
            HasThirdPartyAdoptedContext = evt.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = evt.AdoptedSpeakerIds,
            SupportsInteractiveApproval = evt.SupportsInteractiveApproval ?? channelType.SupportsInteractiveApproval()
        };
    }
}
