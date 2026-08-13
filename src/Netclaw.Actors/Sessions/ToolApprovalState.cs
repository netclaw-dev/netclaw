// -----------------------------------------------------------------------
// <copyright file="ToolApprovalState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

internal sealed record PendingToolInteraction(
    // Tool call id of the parked invocation. Carried explicitly (not just as
    // the _pendingToolInteractions dictionary key) so the record can be
    // persisted in a flat repeated list in the session snapshot.
    string CallId,
    string ToolName,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> CandidateVerbs,
    TrustAudience Audience,
    TrustBoundary? Boundary,
    string? ChannelType,
    bool? SupportsInteractiveApproval,
    string? RequesterSenderId,
    PrincipalClassification? RequesterPrincipal,
    bool HasThirdPartyAdoptedContext,
    IReadOnlyList<string> AdoptedSpeakerIds,
    string? Cwd,
    long RequestedAtMs,
    bool PersistApprovalState,
    TurnContext? TurnContext,
    string? TurnContextRestoreFailure,
    // Option keys that were actually offered to the user when the prompt was
    // rendered. Persisted so a later response cannot select a pruned scope.
    IReadOnlyList<string> OptionKeys,
    // Per-clause (verb, directory) pairs preserved across the pause-for-approval
    // round trip so persistent approvals can write folder-scoped grants from the
    // path arguments the agent originally passed, rather than collapsing to cwd.
    IReadOnlyList<ApprovalCandidate> Candidates,
    string? SessionScratchDirectory) : INoSerializationVerificationNeeded;

// Internal actor message for approval prompts. The request is the public output
// shape; PersistApprovalState is session routing policy that decides whether the
// prompt becomes durable parent-session approval state.
internal sealed record ToolInteractionRequestDispatch(
    SessionProtocol.ToolInteractionRequest Request,
    bool PersistApprovalState) : INoSerializationVerificationNeeded
{
    internal string? SessionScratchDirectory { get; init; }
}

internal abstract record ApprovalTurnState : INoSerializationVerificationNeeded
{
    public static ApprovalTurnState None { get; } = new NoActiveApprovalTurn();
}

internal sealed record NoActiveApprovalTurn : ApprovalTurnState;

internal sealed record RunningApprovalTurn(TurnContext Context) : ApprovalTurnState;

internal sealed record WaitingApprovalTurn(
    TurnContext Context,
    ISet<string> PendingCallIds,
    bool Recovered) : ApprovalTurnState;

internal sealed record RedrivingApprovalTurn(TurnContext Context, string CallId) : ApprovalTurnState;

internal sealed record AbandoningApprovalTurn(TurnContext Context, string Reason) : ApprovalTurnState;

internal sealed record ApprovalRedrivePlan(
    IReadOnlyDictionary<string, IReadOnlyList<string>>? OneTimeApprovalPreSeed,
    IReadOnlyDictionary<string, ApprovalDecision>? DecisionOverride,
    IReadOnlyDictionary<string, string>? SessionScratchDenialDirectories);

internal sealed record ResolvedToolApproval(
    PendingToolInteraction Pending,
    ApprovalDecision Decision);

internal static class ToolApprovalTurnContext
{
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
