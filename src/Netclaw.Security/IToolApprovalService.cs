// -----------------------------------------------------------------------
// <copyright file="IToolApprovalService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;

namespace Netclaw.Security;

public interface IToolApprovalService
{
    /// <summary>
    /// Evaluates candidate <c>(verb, directory)</c> pairs against session and
    /// persistent approvals, returning both misses and the approvals that
    /// satisfied the gate. Shell callers SHOULD prefer this overload so
    /// folder-scoped grants are checked against each candidate's path argument
    /// rather than only the process cwd.
    /// </summary>
    Task<ToolApprovalCheckResult> CheckApprovalAsync(
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        CancellationToken ct = default);

    /// <summary>
    /// Records the grants for the candidates that the operator reviewed, in one
    /// atomic batch. Each grant carries its own scope. The service writes the
    /// persistent grants to the store and every grant to the session.
    /// </summary>
    Task RecordApprovalCandidatesAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        CancellationToken ct = default);
}

/// <summary>
/// One reviewed candidate and the scope that the operator selected for it.
/// </summary>
public sealed record ToolApprovalGrant(
    ApprovalCandidate Candidate,
    GrantScope Scope)
{
    /// <summary>
    /// The worktree root that the grant builder resolved for a
    /// <see cref="GrantScope.Repository"/> grant. The approval actor resolves the
    /// candidate again and refuses the grant when the worktree changed. Other
    /// scopes leave it null.
    /// </summary>
    public string? RepositoryWorktree { get; init; }
}

/// <summary>
/// Approval-service session identity. Kept in the security layer because
/// <c>Netclaw.Security</c> cannot depend on actor protocol types without
/// creating a project-reference cycle.
/// </summary>
public readonly record struct ToolApprovalSessionId(string Value)
{
    public static explicit operator ToolApprovalSessionId(string value) => new(value);

    public override string ToString() => Value;
}

public sealed record ToolApprovalCheckResult(
    IReadOnlyList<string> UnapprovedPatterns,
    IReadOnlyList<ToolApprovalMatch> ApprovedMatches)
{
    /// <summary>
    /// Gets one ordered disposition for each checked candidate. A null value
    /// means the approval service implements the earlier aggregate result.
    /// Callers must retain the full prompt candidate set in that case.
    /// </summary>
    public IReadOnlyList<ToolApprovalCandidateCheck>? CandidateChecks { get; init; }

    /// <summary>
    /// Gets the persistent-store failure, or <c>null</c> when the actor had a
    /// complete persistent snapshot.
    /// </summary>
    public ApprovalStoreFailure? PersistentStoreFailure { get; init; }
}

public sealed record ToolApprovalCandidateCheck(
    ApprovalCandidate Candidate,
    ToolApprovalMatch? ApprovedMatch);

/// <summary>
/// One candidate that a grant covered. <see cref="Pattern"/> is the verb of the
/// candidate (the command words of the call), and <see cref="Scope"/> is the
/// scope of the grant. The words of the grant can be fewer: the grant
/// <c>git push</c> covers the candidate <c>git push origin</c>.
/// </summary>
public sealed record ToolApprovalMatch(
    string Pattern,
    GrantScope Scope);
