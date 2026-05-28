// -----------------------------------------------------------------------
// <copyright file="ToolApprovalState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Configuration;
using Netclaw.Security;

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
    // Option keys that were actually offered to the user when the prompt was
    // rendered. Persisted so a later response cannot select a pruned scope.
    IReadOnlyList<string> OptionKeys,
    // Per-clause (verb, directory) pairs preserved across the pause-for-approval
    // round trip so persistent approvals can write folder-scoped grants from the
    // path arguments the agent originally passed, rather than collapsing to cwd.
    IReadOnlyList<ApprovalCandidate> Candidates) : INoSerializationVerificationNeeded;

// Internal actor message for approval prompts. The request is the public output
// shape; PersistApprovalState is session routing policy that decides whether the
// prompt becomes durable parent-session approval state.
internal sealed record ToolInteractionRequestDispatch(
    Protocol.ToolInteractionRequest Request,
    bool PersistApprovalState) : INoSerializationVerificationNeeded;

internal sealed record ApprovalRedrivePlan(
    IReadOnlyDictionary<string, IReadOnlyList<string>>? OneTimeApprovalPreSeed,
    IReadOnlyDictionary<string, ApprovalDecision>? DecisionOverride);

internal sealed record ResolvedToolApproval(
    PendingToolInteraction Pending,
    ApprovalDecision Decision);
