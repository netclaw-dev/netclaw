// -----------------------------------------------------------------------
// <copyright file="IToolApprovalService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
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
    /// Returns the subset of <paramref name="patterns"/> (candidate verb chains)
    /// that are not approved for the given audience and tool. The
    /// <paramref name="cwd"/> is the candidate's resolved working directory; it
    /// is used by the compatibility matcher to evaluate folder-scoped
    /// <see cref="Netclaw.Configuration.ApprovalEntry"/> records. May be null
    /// for tools whose approvals are not directory-anchored.
    /// </summary>
    Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        string? cwd,
        CancellationToken ct = default);

    Task RecordApprovalAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct = default);
}

/// <summary>
/// Records the structured candidates the user reviewed in one atomic batch.
/// </summary>
public interface IStructuredToolApprovalService
{
    Task RecordApprovalCandidatesAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        bool persistent,
        CancellationToken ct = default);
}

/// <summary>
/// One reviewed candidate and the directory scope selected by the user.
/// </summary>
public sealed record ToolApprovalGrant(
    ApprovalCandidate Candidate,
    string? Directory);

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

public sealed record ToolApprovalMatch(
    string Pattern,
    string Source,
    string Scope);
