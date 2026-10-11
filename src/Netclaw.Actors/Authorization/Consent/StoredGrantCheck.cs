// -----------------------------------------------------------------------
// <copyright file="StoredGrantCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Authorization.Consent;

/// <summary>
/// The result of one stored-grant lookup for a call that is not a shell call.
/// A shell call uses the per-candidate evidence of <see cref="ShellPolicyCoordinator"/> instead.
/// </summary>
/// <param name="Matches">The session and persistent grants that matched.</param>
/// <param name="AllCovered">True when a grant covers every candidate and the per-candidate evidence agrees with the summary.</param>
/// <param name="StoreUnavailableForMiss">True when the persistent store failed and a candidate has no session grant.</param>
internal sealed record StoredGrantCheck(
    IReadOnlyList<ToolApprovalMatch> Matches,
    bool AllCovered,
    bool StoreUnavailableForMiss)
{
    /// <summary>The result when no lookup runs: no approval service, or a request without candidates.</summary>
    internal static StoredGrantCheck NotRun { get; } = new([], AllCovered: false, StoreUnavailableForMiss: false);

    /// <summary>Asks the approval service which grants cover the candidates of one consent request.</summary>
    internal static async Task<StoredGrantCheck> RunAsync(
        IToolApprovalService approvalService,
        ToolName toolName,
        ToolApprovalContext approvalContext,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var candidatesForCheck = approvalContext.Candidates is { Count: > 0 } candidates
            ? candidates.ToList()
            : approvalContext.CandidateVerbs
                .Select(verb => new ApprovalCandidate(verb, Directory: null))
                .ToList();

        if (candidatesForCheck.Count == 0)
            return NotRun;

        var approvalCheck = await approvalService.CheckApprovalAsync(
            ToApprovalSessionId(context.SessionId),
            context.Audience,
            toolName,
            candidatesForCheck,
            context.Approval.Cwd,
            ct);
        var hasExactCandidateChecks = HasExactCandidateChecks(approvalCheck, candidatesForCheck);
        var hasInconsistentCandidateChecks = approvalCheck.CandidateChecks is not null
                                             && !hasExactCandidateChecks;
        return new StoredGrantCheck(
            approvalCheck.ApprovedMatches,
            AllCovered: approvalCheck.UnapprovedPatterns.Count == 0 && !hasInconsistentCandidateChecks,
            StoreUnavailableForMiss: approvalCheck.PersistentStoreFailure is not null
                                     && approvalCheck.UnapprovedPatterns.Count > 0);
    }

    // The per-candidate evidence must repeat the summary exactly, in candidate order.
    // A mismatch means the summary cannot prove coverage, so the call keeps its prompt.
    private static bool HasExactCandidateChecks(
        ToolApprovalCheckResult result,
        IReadOnlyList<ApprovalCandidate> checkedCandidates)
    {
        if (result.CandidateChecks is not { } candidateChecks
            || candidateChecks.Count != checkedCandidates.Count)
        {
            return false;
        }

        var exactUnapprovedPatterns = new List<string>();
        var exactApprovedMatches = new List<ToolApprovalMatch>();
        for (var index = 0; index < candidateChecks.Count; index++)
        {
            var check = candidateChecks[index];
            var checkedCandidate = checkedCandidates[index];
            if (!checkedCandidate.HasSameApprovalFacts(check.Candidate))
                return false;

            if (check.ApprovedMatch is { } approvedMatch)
                exactApprovedMatches.Add(approvedMatch);
            else
                exactUnapprovedPatterns.Add(checkedCandidate.Verb);
        }

        return exactUnapprovedPatterns.SequenceEqual(
                   result.UnapprovedPatterns,
                   StringComparer.OrdinalIgnoreCase)
               && exactApprovedMatches.SequenceEqual(result.ApprovedMatches);
    }

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
