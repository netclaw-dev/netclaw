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

internal sealed class ShellApprovalMatchResult
{
    private ShellApprovalMatchResult(
        ApprovalStoreFailure? persistentStoreFailure,
        ShellGrantCandidateResult[] candidates)
    {
        PersistentStoreFailure = persistentStoreFailure;
        Candidates = Array.AsReadOnly(candidates);
    }

    internal ApprovalStoreFailure? PersistentStoreFailure { get; }

    internal IReadOnlyList<ShellGrantCandidateResult> Candidates { get; }

    internal static ShellApprovalMatchResult Create(
        IReadOnlyList<ShellGrantCandidate> sourceCandidates,
        ApprovalStoreFailure? persistentStoreFailure,
        IReadOnlyList<ShellGrantCandidateResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(sourceCandidates);
        ArgumentNullException.ThrowIfNull(candidates);
        if (persistentStoreFailure is { } failure && !Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(persistentStoreFailure));

        var sourceSnapshot = sourceCandidates.ToArray();
        var candidateSnapshot = candidates.ToArray();
        if (sourceSnapshot.Any(static candidate => candidate is null)
            || candidateSnapshot.Any(static candidate => candidate is null)
            || sourceSnapshot.Length != candidateSnapshot.Length)
        {
            throw new ArgumentException("Shell grant results must match the request candidates.");
        }

        var sourceById = sourceSnapshot.ToDictionary(static candidate => candidate.CandidateId);
        foreach (var candidate in candidateSnapshot)
        {
            if (!sourceById.Remove(candidate.CandidateId, out var source)
                || !candidate.IsFor(source))
            {
                throw new ArgumentException("Shell grant results require each request candidate exactly once.");
            }
        }

        if (persistentStoreFailure is not null
            && candidateSnapshot.Any(static candidate =>
                candidate.HasPersistentEvidence
                || candidate.NearMiss is not null))
        {
            throw new ArgumentException("An unavailable approval store cannot supply persistent evidence.");
        }

        return new ShellApprovalMatchResult(
            persistentStoreFailure,
            candidateSnapshot);
    }
}

internal sealed class ShellGrantCandidateResult
{
    private readonly ApprovalEntry? _persistentGrant;

    private ShellGrantCandidateResult(
        ShellGrantCandidate sourceCandidate,
        ShellCoverageKind coverage,
        ApprovalEntry? persistentGrant,
        ShellApprovalNearMiss? nearMiss)
    {
        SourceCandidate = sourceCandidate;
        Coverage = coverage;
        _persistentGrant = persistentGrant;
        NearMiss = nearMiss;
    }

    internal ShellPolicyCandidateId CandidateId => SourceCandidate.CandidateId;

    private ShellGrantCandidate SourceCandidate { get; }

    internal ShellApprovalNearMiss? NearMiss { get; }

    internal ShellCoverageKind Coverage { get; }

    internal bool HasPersistentEvidence => _persistentGrant is not null;

    internal DateTimeOffset? GrantCreatedAt => _persistentGrant?.CreatedAt;

    internal static ShellGrantCandidateResult Uncovered(
        ShellGrantCandidate candidate,
        ShellApprovalNearMiss? nearMiss = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var validatedNearMiss = nearMiss is null
            ? null
            : ValidateNearMiss(candidate, nearMiss);
        return new ShellGrantCandidateResult(
            candidate,
            ShellCoverageKind.Uncovered,
            persistentGrant: null,
            validatedNearMiss);
    }

    internal static ShellGrantCandidateResult Session(ShellGrantCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new ShellGrantCandidateResult(
            candidate,
            ShellCoverageKind.Session,
            persistentGrant: null,
            nearMiss: null);
    }

    internal static ShellGrantCandidateResult Persistent(
        ShellGrantCandidate candidate,
        ApprovalEntry grant)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var validatedGrant = ValidateShellGrant(grant);
        if (!ApprovalPatternMatching.MatchesShellApproval(
                candidate.Candidate,
                candidate.RealDirectory,
                [validatedGrant]))
        {
            throw new ArgumentException(
                "The persistent grant does not match its shell candidate.",
                nameof(grant));
        }

        return new ShellGrantCandidateResult(
            candidate,
            PersistentCoverage(validatedGrant),
            validatedGrant,
            nearMiss: null);
    }

    internal bool IsFor(ShellGrantCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (CandidateId != candidate.CandidateId
            || !string.Equals(
                SourceCandidate.RealDirectory,
                candidate.RealDirectory,
                StringComparison.Ordinal)
            || !SourceCandidate.Candidate.HasSameApprovalFacts(candidate.Candidate))
        {
            return false;
        }

        if (_persistentGrant is { } grant)
        {
            return ApprovalPatternMatching.MatchesShellApproval(
                candidate.Candidate,
                candidate.RealDirectory,
                [grant]);
        }

        return NearMiss is not { } nearMiss || IsNearMissFor(candidate, nearMiss);
    }

    internal ToolApprovalMatch FormatMatch(ApprovalCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Coverage switch
        {
            ShellCoverageKind.Session =>
                new ToolApprovalMatch(candidate.Verb, "session", "this chat"),
            ShellCoverageKind.PersistentGlobal
                or ShellCoverageKind.PersistentFolder
                or ShellCoverageKind.PersistentRepository =>
                new ToolApprovalMatch(candidate.Verb, "persistent", _persistentGrant!.FormatScope()),
            _ => throw new InvalidOperationException("An uncovered candidate has no approval match."),
        };
    }

    private static ShellCoverageKind PersistentCoverage(ApprovalEntry grant)
        => grant.Repository is not null
            ? ShellCoverageKind.PersistentRepository
            : grant.Directory is null
                ? ShellCoverageKind.PersistentGlobal
                : ShellCoverageKind.PersistentFolder;

    private static ShellApprovalNearMiss ValidateNearMiss(
        ShellGrantCandidate candidate,
        ShellApprovalNearMiss nearMiss)
    {
        ArgumentNullException.ThrowIfNull(nearMiss);
        if (!Enum.IsDefined(nearMiss.Reason))
            throw new ArgumentOutOfRangeException(nameof(nearMiss));

        var grant = ValidateShellGrant(nearMiss.Grant);
        var validated = new ShellApprovalNearMiss(grant, nearMiss.Reason);
        if (!IsNearMissFor(candidate, validated))
        {
            throw new ArgumentException("The near miss does not match its shell candidate.", nameof(nearMiss));
        }

        return validated;
    }

    private static bool IsNearMissFor(
        ShellGrantCandidate candidate,
        ShellApprovalNearMiss nearMiss)
    {
        var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
            candidate.Candidate,
            candidate.RealDirectory,
            [nearMiss.Grant],
            maximumNearMisses: 1);
        return evaluation.MatchedEntry is null
               && evaluation.NearMisses.Count == 1
               && evaluation.NearMisses[0].Reason == nearMiss.Reason
               && ToolApprovalEntryComparer.Equals(
                   evaluation.NearMisses[0].Grant,
                   nearMiss.Grant);
    }

    private static ApprovalEntry ValidateShellGrant(ApprovalEntry grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ApprovalEntryValidation.ValidateVersion3(grant);
        if (grant.Shell is null || grant.Match is null)
            throw new ArgumentException("The approval entry is not a shell grant.", nameof(grant));

        return grant;
    }

}
