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
            return CreateEmptyResult(request.Candidates, persistentStoreFailure: null);

        if (approvalService is IShellApprovalMatchService shellApprovalService)
        {
            var result = await shellApprovalService.MatchShellCandidatesAsync(
                request,
                cancellationToken);
            return RequireMatchingRequest(result, request.Candidates);
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
            return CreateEmptyResult(candidates, result.PersistentStoreFailure);
        }

        if (checks.Count != candidates.Count)
            throw new InvalidOperationException("The approval service returned the wrong candidate count.");

        var candidateResults = new ShellGrantCandidateResult[checks.Count];
        var unapprovedPatterns = new List<string>();
        var approvedMatches = new List<ToolApprovalMatch>();
        for (var index = 0; index < checks.Count; index++)
        {
            var expected = candidates[index];
            var check = checks[index]
                ?? throw new InvalidOperationException("The approval service returned a null candidate check.");
            if (!HasSameCandidateFacts(check.Candidate, expected.Candidate))
                throw new InvalidOperationException("The approval service changed candidate facts.");

            if (check.ApprovedMatch is not { } approvedMatch)
            {
                unapprovedPatterns.Add(expected.Candidate.Verb);
                candidateResults[index] = ShellGrantCandidateResult.Uncovered(expected);
                continue;
            }

            if (!string.Equals(
                    approvedMatch.Pattern,
                    expected.Candidate.Verb,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The approval service changed the matched shell phrase.");
            }

            approvedMatches.Add(approvedMatch);
            candidateResults[index] = approvedMatch.Source switch
            {
                "session" when string.Equals(
                    approvedMatch.Scope,
                    "this chat",
                    StringComparison.Ordinal) => ShellGrantCandidateResult.Session(expected),
                "persistent" => CreatePersistentResult(expected, approvedMatch.Scope),
                _ => throw new InvalidOperationException(
                    "The approval service returned an unknown grant source."),
            };
        }

        if (!unapprovedPatterns.SequenceEqual(
                result.UnapprovedPatterns,
                StringComparer.OrdinalIgnoreCase)
            || !approvedMatches.SequenceEqual(result.ApprovedMatches))
        {
            throw new InvalidOperationException("The approval service returned inconsistent aggregates.");
        }

        return ShellApprovalMatchResult.Create(
            candidates,
            result.PersistentStoreFailure,
            candidateResults);
    }

    private static ShellGrantCandidateResult CreatePersistentResult(
        ShellGrantCandidate candidate,
        string scope)
    {
        if (!ApprovalEntry.TryParseScope(scope, out var entry, out _)
            || !string.Equals(entry.FormatScope(), scope, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The approval service returned a malformed grant scope.");
        }

        try
        {
            return ShellGrantCandidateResult.Persistent(candidate, entry);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(
                "The approval service returned an invalid persistent grant.",
                exception);
        }
    }

    private static ShellApprovalMatchResult CreateEmptyResult(
        IReadOnlyList<ShellGrantCandidate> candidates,
        ApprovalStoreFailure? persistentStoreFailure)
        => ShellApprovalMatchResult.Create(
            candidates,
            persistentStoreFailure,
            candidates
                .Select(static candidate => ShellGrantCandidateResult.Uncovered(candidate))
                .ToArray());

    private static ShellApprovalMatchResult RequireMatchingRequest(
        ShellApprovalMatchResult result,
        IReadOnlyList<ShellGrantCandidate> expected)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ShellApprovalMatchResult.Create(
            expected,
            result.PersistentStoreFailure,
            result.Candidates);
    }

    private static bool HasSameCandidateFacts(
        ApprovalCandidate first,
        ApprovalCandidate second) =>
        string.Equals(first.Verb, second.Verb, StringComparison.Ordinal) &&
        string.Equals(first.Directory, second.Directory, StringComparison.Ordinal) &&
        first.Shell == second.Shell &&
        first.AssignmentDigest == second.AssignmentDigest &&
        ((first.VerbTokens is null && second.VerbTokens is null) ||
         (first.VerbTokens is not null &&
          second.VerbTokens is not null &&
          first.VerbTokens.SequenceEqual(second.VerbTokens, StringComparer.Ordinal)));
}
