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
    string? Cwd,
    long RequestedAtMs,
    // Option keys that were actually offered to the user when the prompt was
    // rendered. Persisted so a later response cannot select a pruned scope.
    IReadOnlyList<string> OptionKeys,
    // Per-clause (verb, directory) pairs preserved across the pause-for-approval
    // round trip so persistent approvals can write folder-scoped grants from the
    // path arguments the agent originally passed, rather than collapsing to cwd.
    IReadOnlyList<ApprovalCandidate> Candidates) : INoSerializationVerificationNeeded;

internal sealed record ApprovalRedrivePlan(
    IReadOnlyDictionary<string, IReadOnlyList<string>>? OneTimeApprovalPreSeed,
    IReadOnlyDictionary<string, ApprovalDecision>? DecisionOverride);

internal sealed record ResolvedToolApproval(
    PendingToolInteraction Pending,
    ApprovalDecision Decision);
