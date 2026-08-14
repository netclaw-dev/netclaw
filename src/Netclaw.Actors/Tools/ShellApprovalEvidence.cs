// -----------------------------------------------------------------------
// <copyright file="ShellApprovalEvidence.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ShellApprovalEvidenceAdapter(IToolApprovalService? approvalService)
{
    internal bool IsAvailable => approvalService is not null;

    internal async Task<ShellApprovalMatchResult> MatchAsync(
        ShellApprovalMatchRequest request,
        string? cwd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Candidates.Count == 0 || approvalService is null)
            return CreateEmptyResult(
                request.Candidates,
                new PersistentGrantStoreStatus.Ready());

        if (approvalService is IShellApprovalMatchService shellApprovalService)
        {
            return await shellApprovalService.MatchShellCandidatesAsync(
                request,
                cancellationToken);
        }

        var compatibilityResult = await approvalService.CheckApprovalAsync(
            request.SessionId,
            request.Audience,
            request.ToolName,
            request.Candidates.Select(static candidate => candidate.Candidate).ToArray(),
            cwd,
            cancellationToken);
        return ConvertCompatibilityResult(compatibilityResult, request.Candidates);
    }

    private static ShellApprovalMatchResult ConvertCompatibilityResult(
        ToolApprovalCheckResult result,
        IReadOnlyList<ShellGrantCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CandidateChecks is not { } checks)
        {
            var aggregateStoreStatus = result.PersistentStoreFailure is { } aggregateFailure
                ? (PersistentGrantStoreStatus)new PersistentGrantStoreStatus.Unavailable(aggregateFailure)
                : new PersistentGrantStoreStatus.Ready();
            return CreateEmptyResult(candidates, aggregateStoreStatus);
        }

        if (checks.Count != candidates.Count)
            throw new InvalidOperationException("The approval service returned the wrong candidate count.");

        var matches = new ShellGrantCandidateMatch[checks.Count];
        var unapprovedPatterns = new List<string>();
        var approvedMatches = new List<ToolApprovalMatch>();
        for (var index = 0; index < checks.Count; index++)
        {
            var expected = candidates[index];
            var check = checks[index]
                ?? throw new InvalidOperationException("The approval service returned a null candidate check.");
            if (!HasSameCandidateFacts(check.Candidate, expected.Candidate))
                throw new InvalidOperationException("The approval service changed candidate facts.");

            ShellCoverageKind? grantCoverage = null;
            if (check.ApprovedMatch is { } approvedMatch)
            {
                approvedMatches.Add(approvedMatch);
                grantCoverage = approvedMatch.Source switch
                {
                    "session" => ShellCoverageKind.Session,
                    "persistent" when approvedMatch.Scope.EndsWith(" anywhere", StringComparison.Ordinal) =>
                        ShellCoverageKind.PersistentGlobal,
                    "persistent" => ShellCoverageKind.PersistentFolder,
                    _ => throw new InvalidOperationException("The approval service returned an unknown grant source."),
                };
            }
            else
            {
                unapprovedPatterns.Add(expected.Candidate.Verb);
            }

            matches[index] = new ShellGrantCandidateMatch(
                expected.CandidateId,
                check.ApprovedMatch,
                grantCoverage,
                []);
        }

        if (!unapprovedPatterns.SequenceEqual(
                result.UnapprovedPatterns,
                StringComparer.OrdinalIgnoreCase)
            || !approvedMatches.SequenceEqual(result.ApprovedMatches))
        {
            throw new InvalidOperationException("The approval service returned inconsistent aggregates.");
        }

        var storeStatus = result.PersistentStoreFailure is { } failure
            ? (PersistentGrantStoreStatus)new PersistentGrantStoreStatus.Unavailable(failure)
            : new PersistentGrantStoreStatus.Ready();
        return new ShellApprovalMatchResult(
            storeStatus,
            Array.AsReadOnly(matches));
    }

    private static ShellApprovalMatchResult CreateEmptyResult(
        IReadOnlyList<ShellGrantCandidate> candidates,
        PersistentGrantStoreStatus storeStatus)
        => new(
            storeStatus,
            Array.AsReadOnly(candidates
                .Select(static candidate => new ShellGrantCandidateMatch(
                    candidate.CandidateId,
                    Match: null,
                    GrantCoverage: null,
                    NearMisses: []))
                .ToArray()));

    private static bool HasSameCandidateFacts(
        ApprovalCandidate first,
        ApprovalCandidate second) =>
        string.Equals(first.Verb, second.Verb, StringComparison.Ordinal) &&
        string.Equals(first.Directory, second.Directory, StringComparison.Ordinal) &&
        first.Shell == second.Shell &&
        ((first.VerbTokens is null && second.VerbTokens is null) ||
         (first.VerbTokens is not null &&
          second.VerbTokens is not null &&
          first.VerbTokens.SequenceEqual(second.VerbTokens, StringComparer.Ordinal)));
}

internal sealed class ValidatedShellGrantEvidence
{
    private ValidatedShellGrantEvidence(
        PersistentGrantStoreStatus persistentStore,
        IReadOnlyList<ShellPolicyCandidate> sourceCandidates,
        ValidatedShellGrantCandidateEvidence[] candidateEvidence,
        ToolApprovalMatch[] approvalMatches)
    {
        PersistentStore = persistentStore;
        SourceCandidates = sourceCandidates;
        CandidateEvidence = Array.AsReadOnly(candidateEvidence);
        ApprovalMatches = Array.AsReadOnly(approvalMatches);
    }

    internal PersistentGrantStoreStatus PersistentStore { get; }

    internal IReadOnlyList<ShellPolicyCandidate> SourceCandidates { get; }

    internal IReadOnlyList<ValidatedShellGrantCandidateEvidence> CandidateEvidence { get; }

    internal IReadOnlyList<ToolApprovalMatch> ApprovalMatches { get; }

    internal static bool TryCreate(
        ShellApprovalMatchResult result,
        IReadOnlyList<ShellPolicyCandidate> candidates,
        string? cwd,
        out ValidatedShellGrantEvidence? evidence)
    {
        evidence = null;
        if (result is null
            || candidates is null
            || !TryValidateStore(result.PersistentStore, out var storeUnavailable)
            || result.CandidateMatches is null)
        {
            return false;
        }

        var candidateMatches = result.CandidateMatches.ToArray();
        if (candidateMatches.Length != candidates.Count)
            return false;

        var expectedById = candidates.ToDictionary(static candidate => candidate.Id);
        var validated = new ValidatedShellGrantCandidateEvidence[candidates.Count];
        var approvalMatches = new List<ToolApprovalMatch>(candidates.Count);
        for (var index = 0; index < candidateMatches.Length; index++)
        {
            var candidateMatch = candidateMatches[index];
            if (!TryCopyCandidateEvidence(candidateMatch, out var candidateEvidence)
                || candidateEvidence is null
                || !expectedById.Remove(candidateEvidence.CandidateId, out var candidate)
                || !TryValidateCandidateEvidence(
                    candidate,
                    candidateEvidence,
                    cwd,
                    storeUnavailable))
            {
                return false;
            }

            validated[index] = new ValidatedShellGrantCandidateEvidence(candidate, candidateEvidence);
            if (candidateEvidence.Match is { } match)
                approvalMatches.Add(match);
        }

        evidence = new ValidatedShellGrantEvidence(
            result.PersistentStore,
            candidates,
            validated,
            approvalMatches.ToArray());
        return true;
    }

    private static bool TryValidateStore(
        PersistentGrantStoreStatus persistentStore,
        out bool unavailable)
    {
        unavailable = false;
        switch (persistentStore)
        {
            case PersistentGrantStoreStatus.Ready:
                return true;
            case PersistentGrantStoreStatus.Unavailable storeUnavailable
                when Enum.IsDefined(storeUnavailable.Failure):
                unavailable = true;
                return true;
            default:
                return false;
        }
    }

    private static bool TryValidateCandidateEvidence(
        ShellPolicyCandidate candidate,
        ShellGrantCandidateMatch candidateMatch,
        string? cwd,
        bool storeUnavailable)
    {
        if (candidateMatch.NearMisses is null)
            return false;

        if (candidateMatch.Match is null)
        {
            return candidateMatch.GrantCoverage is null
                   && candidateMatch.GrantCreatedAt is null
                   && candidateMatch.NearMisses.Count <= 1
                   && (!storeUnavailable || candidateMatch.NearMisses.Count == 0)
                   && candidateMatch.NearMisses.All(nearMiss =>
                       IsConsistentNearMiss(candidate.Candidate, nearMiss, cwd));
        }

        if (candidateMatch.GrantCoverage is not
            (ShellCoverageKind.Session
            or ShellCoverageKind.PersistentGlobal
            or ShellCoverageKind.PersistentFolder)
            || candidateMatch.NearMisses.Count != 0
            || (candidateMatch.GrantCoverage == ShellCoverageKind.Session
                && candidateMatch.GrantCreatedAt is not null)
            || (storeUnavailable
                && candidateMatch.GrantCoverage is
                    (ShellCoverageKind.PersistentGlobal or ShellCoverageKind.PersistentFolder)))
        {
            return false;
        }

        return IsConsistentActorMatch(
            candidate.Candidate,
            candidateMatch.Match,
            candidateMatch.GrantCoverage.Value,
            cwd);
    }

    private static bool TryCopyCandidateEvidence(
        ShellGrantCandidateMatch? source,
        out ShellGrantCandidateMatch? snapshot)
    {
        snapshot = null;
        if (source?.NearMisses is null)
            return false;

        var nearMisses = source.NearMisses.ToArray();
        if (nearMisses.Any(static nearMiss => nearMiss is null))
            return false;

        snapshot = source with { NearMisses = Array.AsReadOnly(nearMisses) };
        return true;
    }

    private static bool IsConsistentActorMatch(
        ApprovalCandidate candidate,
        ToolApprovalMatch match,
        ShellCoverageKind coverage,
        string? cwd)
    {
        if (match.Pattern is null
            || match.Source is null
            || match.Scope is null
            || !string.Equals(match.Pattern, candidate.Verb, StringComparison.Ordinal))
        {
            return false;
        }

        if (coverage == ShellCoverageKind.Session)
        {
            return string.Equals(match.Source, "session", StringComparison.Ordinal)
                   && string.Equals(match.Scope, "this chat", StringComparison.Ordinal);
        }

        if (coverage is not
            (ShellCoverageKind.PersistentGlobal or ShellCoverageKind.PersistentFolder)
            || !string.Equals(match.Source, "persistent", StringComparison.Ordinal)
            || !ApprovalEntry.TryParseScope(match.Scope, out var entry, out _)
            || !string.Equals(entry.FormatScope(), match.Scope, StringComparison.Ordinal)
            || !IsCanonicalShellEntry(entry)
            || entry.Shell != candidate.Shell
            || entry.Match is null
            || (coverage == ShellCoverageKind.PersistentGlobal) != (entry.Directory is null))
        {
            return false;
        }

        return ApprovalPatternMatching.MatchesShellApproval(candidate, cwd, [entry]);
    }

    private static bool IsConsistentNearMiss(
        ApprovalCandidate candidate,
        ShellApprovalNearMiss nearMiss,
        string? cwd)
    {
        if (nearMiss is null || !Enum.IsDefined(nearMiss.Reason) || nearMiss.Grant is null)
            return false;

        try
        {
            var scope = nearMiss.Grant.FormatScope();
            if (!ApprovalEntry.TryParseScope(scope, out var canonicalGrant, out _)
                || !HasSameEntryFacts(nearMiss.Grant, canonicalGrant)
                || !IsCanonicalShellEntry(canonicalGrant))
            {
                return false;
            }

            var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
                candidate,
                cwd,
                [nearMiss.Grant],
                maximumNearMisses: 1);
            return evaluation.MatchedEntry is null
                   && evaluation.NearMisses.Count == 1
                   && evaluation.NearMisses[0].Reason == nearMiss.Reason
                   && ToolApprovalEntryComparer.Equals(
                       evaluation.NearMisses[0].Grant,
                       nearMiss.Grant);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    private static bool HasSameEntryFacts(ApprovalEntry first, ApprovalEntry second)
        => string.Equals(first.Verb, second.Verb, StringComparison.Ordinal)
           && string.Equals(first.Directory, second.Directory, StringComparison.Ordinal)
           && first.Shell == second.Shell
           && first.Match == second.Match
           && ((first.VerbTokens is null && second.VerbTokens is null)
               || (first.VerbTokens is not null
                   && second.VerbTokens is not null
                   && first.VerbTokens.SequenceEqual(second.VerbTokens, StringComparer.Ordinal)));

    private static bool IsCanonicalShellEntry(ApprovalEntry entry)
    {
        if (entry.Shell is null || entry.Match is null)
        {
            return false;
        }

        try
        {
            ApprovalEntryValidation.ValidateVersion3(entry);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

internal sealed record ValidatedShellGrantCandidateEvidence(
    ShellPolicyCandidate Candidate,
    ShellGrantCandidateMatch ActorEvidence);
