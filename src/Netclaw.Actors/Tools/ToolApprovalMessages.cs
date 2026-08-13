// -----------------------------------------------------------------------
// <copyright file="ToolApprovalMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Internal cross-actor message contract between the session pipeline and
/// <c>ToolApprovalActor</c>. Assembly-internal: not a public protocol surface.
/// </summary>
internal static class ToolApprovalProtocol
{
    /// <summary>Marker for tool-approval commands.</summary>
    internal interface IToolApprovalCommand;

    /// <summary>Marker for tool-approval queries.</summary>
    internal interface IToolApprovalQuery;

    /// <summary>Marker for tool-approval responses.</summary>
    internal interface IToolApprovalResponse;

    // ===== Queries =====

    internal sealed record GetUnapprovedPatterns(
        SessionId? SessionId,
        TrustAudience Audience,
        ToolName ToolName,
        IReadOnlyList<ApprovalCandidate> Candidates,
        string? Cwd) : IToolApprovalQuery;

    internal sealed record MatchShellCandidates(
        SessionId? SessionId,
        TrustAudience Audience,
        ToolName ToolName,
        ShellExecutionEnvironment Environment,
        IReadOnlyList<ShellGrantCandidate> Candidates) : IToolApprovalQuery;

    // ===== Responses =====

    internal sealed record UnapprovedPatternsResponse(ToolApprovalCheckResult Result) : IToolApprovalResponse;

    internal sealed record ShellApprovalMatchResponse(
        ShellApprovalMatchResult Result) : IToolApprovalResponse;

    // ===== Commands =====

    internal sealed record RecordToolApproval(
        SessionId SessionId,
        TrustAudience Audience,
        ToolName ToolName,
        IReadOnlyList<string> Patterns,
        bool Persistent,
        string? Cwd) : IToolApprovalCommand;

    internal sealed record RecordStructuredToolApproval(
        SessionId SessionId,
        TrustAudience Audience,
        ToolName ToolName,
        IReadOnlyList<ToolApprovalGrant> Grants,
        bool Persistent) : IToolApprovalCommand;
}

internal interface IShellApprovalMatchService
{
    Task<ShellApprovalMatchResult> MatchShellCandidatesAsync(
        ShellApprovalMatchRequest request,
        CancellationToken cancellationToken);
}

internal sealed record ShellApprovalMatchRequest(
    ToolApprovalSessionId? SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    ShellExecutionEnvironment Environment,
    IReadOnlyList<ShellGrantCandidate> Candidates);

internal sealed record ShellGrantCandidate(
    ShellPolicyCandidateId CandidateId,
    ApprovalCandidate Candidate,
    string? RealDirectory);

internal sealed record ShellApprovalMatchResult(
    PersistentGrantStoreStatus PersistentStore,
    IReadOnlyList<ShellGrantCandidateMatch> CandidateMatches);

internal abstract record PersistentGrantStoreStatus
{
    private PersistentGrantStoreStatus()
    {
    }

    internal sealed record Ready : PersistentGrantStoreStatus;

    internal sealed record Unavailable(ApprovalStoreFailure Failure) : PersistentGrantStoreStatus;
}

internal sealed record ShellGrantCandidateMatch(
    ShellPolicyCandidateId CandidateId,
    ToolApprovalMatch? Match,
    ShellCoverageKind? GrantCoverage,
    IReadOnlyList<ShellApprovalNearMiss> NearMisses)
{
    internal DateTimeOffset? GrantCreatedAt { get; init; }
}
