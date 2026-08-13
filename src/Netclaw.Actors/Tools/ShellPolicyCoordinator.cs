// -----------------------------------------------------------------------
// <copyright file="ShellPolicyCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Coordinates shell preflight, one approval-store check, and final policy.
/// </summary>
internal sealed class ShellPolicyCoordinator(
    ToolAccessPolicy policy,
    IToolApprovalService? approvalService)
{
    internal async Task<ToolAuthorizationDecision> EvaluateAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var trace = new ShellPolicyDecisionTraceBuilder();
        try
        {
            return await EvaluateCoreAsync(tool, toolCall, context, trace, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace);
        }
    }

    private async Task<ToolAuthorizationDecision> EvaluateCoreAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyDecisionTraceBuilder trace,
        CancellationToken cancellationToken)
    {
        var preflight = policy.AuthorizeShellPreflight(tool, context, toolCall.Arguments);
        if (!preflight.NeedsApproval)
            return Complete(preflight, [], trace);

        var approvalContext = preflight.ApprovalContext;
        policy.TryGetAuthorizedShellAnalysis(context, out var execution);
        if (approvalContext is null
            || !ShellPolicyProjection.TryCreate(
                policy.ShellEnvironment,
                policy.ShellApprovalMatcher,
                execution,
                approvalContext,
                context,
                policy.IsSafePlatformTemporaryPath,
                out var projection)
            || projection is null)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace);
        }

        return await CompleteAsync(
            tool,
            toolCall,
            context,
            projection,
            trace,
            cancellationToken);
    }

    private async Task<ToolAuthorizationDecision> CompleteAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyProjection projection,
        ShellPolicyDecisionTraceBuilder trace,
        CancellationToken cancellationToken)
    {
        if (projection.ApprovalContext.IsMessy && !projection.HasCausalIntent)
            return CompleteOneTimeOrPrompt(toolCall.Name, projection, projection.ApprovalContext, [], trace);

        if (projection.Candidates.Count == 0)
            return CompleteOneTimeOrPrompt(toolCall.Name, projection, projection.ApprovalContext, [], trace);

        var expectedShell = projection.Environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        if (projection.Candidates.Any(candidate => candidate.Candidate.Shell is null
                                                    || candidate.Candidate.VerbTokens is null))
        {
            return CompleteOneTimeOrPrompt(toolCall.Name, projection, projection.ApprovalContext, [], trace);
        }

        if (projection.Candidates.Any(candidate =>
                candidate.Candidate.Shell != expectedShell
                || candidate.Candidate.VerbTokens!.Count == 0
                || candidate.Candidate.VerbTokens!.Any(static token =>
                    token.Length == 0 || token.Any(char.IsWhiteSpace))))
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace);
        }

        if (projection.Candidates.Any(candidate =>
                candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                && candidate.IntentDirectory is { } intentDirectory
                && candidate.SourceOccurrence is { } sourceOccurrence
                && policy.CausalIntentReferencesProtectedPath(
                    sourceOccurrence,
                    intentDirectory,
                    candidate.IntentFallbackDirectories)))
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("shell_references_protected_path"),
                trace);
        }

        if (projection.Candidates.Any(candidate =>
                candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                && candidate.IntentDirectory is { } intentDirectory
                && !policy.AreCausalIntentDirectoriesEligible(
                    intentDirectory,
                    candidate.IntentFallbackDirectories)))
        {
            return CompleteOneTimeOrPrompt(
                toolCall.Name,
                projection,
                projection.ApprovalContext,
                [],
                trace);
        }

        var coverage = new ShellCoverageSet(projection.Candidates);
        foreach (var candidate in projection.Candidates.Where(item =>
                     approvalService is not null
                     && item.Role == ShellPolicyCandidateRole.Ordinary
                     && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
        {
            coverage.Cover(
                candidate.Id,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ApprovalExemptSideEffect);
        }

        var grantCandidates = projection.GrantCandidates;
        var actorResult = await MatchCandidatesAsync(
            tool,
            context,
            projection,
            grantCandidates,
            cancellationToken);
        if (!TryApplyActorResult(
                actorResult,
                grantCandidates,
                projection.ApprovalContext.Cwd,
                coverage,
                out var approvalMatches))
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace);
        }

        foreach (var actorMatch in actorResult.CandidateMatches)
        {
            var candidate = grantCandidates.First(item => item.Id == actorMatch.CandidateId);
            trace.AddActorEvidence(candidate, actorMatch);
        }

        foreach (var candidate in projection.Candidates.Where(item =>
                     approvalService is not null
                     && item.Role == ShellPolicyCandidateRole.Ordinary
                     && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
        {
            trace.AddCoverage(
                ShellPolicyTraceStage.ReviewedSafePolicy,
                candidate,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ApprovalExemptSideEffect,
                ShellScopeRelation.None);
        }

        var canUseReviewedSafePolicy =
            projection.RunScope.InteractiveApproval is InteractiveApprovalCapability.Available;
        foreach (var candidate in grantCandidates.Where(candidate =>
                     canUseReviewedSafePolicy
                     && candidate.CanUseRealReviewedSafePolicy
                     && coverage.UncoveredIds.Contains(candidate.Id)))
        {
            if (policy.IsReviewedSafeCandidate(
                    candidate.Candidate,
                    candidate.SourceOccurrence,
                    projection.ApprovalContext.Cwd,
                    context.Invocation))
            {
                coverage.Cover(
                    candidate.Id,
                    ShellCoverageKind.ReviewedSafePolicy,
                    ShellPolicyReason.ReviewedSafePhrase);
                trace.AddCoverage(
                    ShellPolicyTraceStage.ReviewedSafePolicy,
                    candidate,
                    ShellCoverageKind.ReviewedSafePolicy,
                    ShellPolicyReason.ReviewedSafePhrase,
                    ShellScopeRelation.UnderRealRoot);
            }
        }

        foreach (var candidate in projection.Candidates.Where(candidate =>
                     canUseReviewedSafePolicy
                     && candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                     && coverage.UncoveredIds.Contains(candidate.Id)))
        {
            if (candidate.IntentDirectory is null
                || candidate.IntentPrerequisites.Count == 0
                || candidate.IntentPrerequisites.Any(prerequisite =>
                    !coverage.IsCovered(prerequisite))
                || !policy.IsReviewedSafeIntentCandidate(
                    candidate.Candidate,
                    candidate.SourceOccurrence,
                    candidate.IntentDirectory,
                    context.Invocation))
            {
                continue;
            }

            coverage.Cover(
                candidate.Id,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ReviewedSafePhrase);
            trace.AddCoverage(
                ShellPolicyTraceStage.ReviewedSafePolicy,
                candidate,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ReviewedSafePhrase,
                ShellScopeRelation.UnderIntentRoot);
        }

        var uncovered = GetUncoveredCandidates(projection, coverage);
        var oneTimeApplied = false;
        if (uncovered.Count > 0)
        {
            var remainingContext = projection.HasCausalIntent
                ? projection.ApprovalContext
                : ToolAccessPolicy.NarrowShellApprovalContext(
                    projection.ApprovalContext,
                    uncovered.Select(static candidate => candidate.Candidate).ToArray(),
                    context.SessionDirectory,
                    projection.Environment.PathStyle);
            if (projection.HasExactOneTimeApproval(toolCall.Name, remainingContext))
            {
                foreach (var candidate in uncovered)
                {
                    coverage.Cover(
                        candidate.Id,
                        ShellCoverageKind.OneTime,
                        ShellPolicyReason.OneTimeGrant);
                    trace.AddCoverage(
                        ShellPolicyTraceStage.OneTimeApproval,
                        candidate,
                        ShellCoverageKind.OneTime,
                        ShellPolicyReason.OneTimeGrant,
                        ShellScopeRelation.None);
                }

                uncovered = [];
                oneTimeApplied = true;
            }
        }

        if (uncovered.Count > 0
            && actorResult.PersistentStore is PersistentGrantStoreStatus.Unavailable)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("approval_store_unavailable"),
                trace);
        }

        if (uncovered.Count > 0)
        {
            var promptContext = projection.HasCausalIntent
                ? projection.ApprovalContext
                : ToolAccessPolicy.NarrowShellApprovalContext(
                    projection.ApprovalContext,
                    uncovered.Select(static candidate => candidate.Candidate).ToArray(),
                    context.SessionDirectory,
                    projection.Environment.PathStyle);
            return CompleteWithTrace(
                ToolAuthorizationDecision.RequiresApproval(promptContext, approvalMatches),
                trace);
        }

        if (!coverage.AllCovered)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace);
        }

        if (oneTimeApplied)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.OneTimeApproval,
                    approvalMatches),
                trace);
        }

        if (approvalMatches.Count > 0)
        {
            if (approvalMatches.Count == grantCandidates.Count)
            {
                context.Approval.ApplyDecision(
                    "PreviouslyApproved",
                    FormatApprovalMatches(approvalMatches));
            }

            return CompleteWithTrace(
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.StoredApproval,
                    approvalMatches),
                trace);
        }

        return CompleteWithTrace(
            ToolAuthorizationDecision.Allow(
                grantCandidates.Count == 0
                    ? ToolAllowReason.ApprovalExemptShellCandidates
                    : ToolAllowReason.SafeVerbInTrustedScope),
            trace);
    }

    private async Task<ShellApprovalMatchResult> MatchCandidatesAsync(
        INetclawTool tool,
        ToolExecutionContext context,
        ShellPolicyProjection projection,
        IReadOnlyList<ShellPolicyCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || approvalService is null)
        {
            return CreateEmptyMatchResult(candidates);
        }

        var requestCandidates = candidates
            .Select(candidate => new ShellGrantCandidate(
                candidate.Id,
                candidate.Candidate,
                projection.ApprovalContext.Cwd))
            .ToArray();
        if (approvalService is IShellApprovalMatchService shellApprovalService)
        {
            return await shellApprovalService.MatchShellCandidatesAsync(
                new ShellApprovalMatchRequest(
                    ToApprovalSessionId(context.SessionId),
                    context.Audience,
                    new ToolName(tool.Name),
                    projection.Environment,
                    Array.AsReadOnly(requestCandidates)),
                cancellationToken);
        }

        var compatibilityResult = await approvalService.CheckApprovalAsync(
            ToApprovalSessionId(context.SessionId),
            context.Audience,
            new ToolName(tool.Name),
            candidates.Select(static candidate => candidate.Candidate).ToArray(),
            projection.ApprovalContext.Cwd,
            cancellationToken);
        return ConvertCompatibilityResult(compatibilityResult, candidates);
    }

    private static ShellApprovalMatchResult ConvertCompatibilityResult(
        ToolApprovalCheckResult result,
        IReadOnlyList<ShellPolicyCandidate> candidates)
    {
        if (result.CandidateChecks is not { } checks)
        {
            var aggregateStoreStatus = result.PersistentStoreFailure is { } aggregateFailure
                ? (PersistentGrantStoreStatus)new PersistentGrantStoreStatus.Unavailable(aggregateFailure)
                : new PersistentGrantStoreStatus.Ready();
            return new ShellApprovalMatchResult(
                aggregateStoreStatus,
                Array.AsReadOnly(candidates
                    .Select(static candidate => new ShellGrantCandidateMatch(
                        candidate.Id,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses: []))
                    .ToArray()));
        }

        if (checks.Count != candidates.Count)
            throw new InvalidOperationException("The approval service returned the wrong candidate count.");

        var matches = new ShellGrantCandidateMatch[checks.Count];
        var unapprovedPatterns = new List<string>();
        var approvedMatches = new List<ToolApprovalMatch>();
        for (var index = 0; index < checks.Count; index++)
        {
            var expected = candidates[index];
            var check = checks[index];
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
                expected.Id,
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

    private static bool TryApplyActorResult(
        ShellApprovalMatchResult result,
        IReadOnlyList<ShellPolicyCandidate> candidates,
        string? cwd,
        ShellCoverageSet coverage,
        out IReadOnlyList<ToolApprovalMatch> approvalMatches)
    {
        approvalMatches = [];
        if (result.PersistentStore is PersistentGrantStoreStatus.Unavailable unavailable
            && !Enum.IsDefined(unavailable.Failure))
        {
            return false;
        }

        if (result.PersistentStore is not PersistentGrantStoreStatus.Ready
            && result.PersistentStore is not PersistentGrantStoreStatus.Unavailable)
        {
            return false;
        }

        if (result.CandidateMatches.Count != candidates.Count)
            return false;

        var expectedIds = candidates.Select(static candidate => candidate.Id).ToHashSet();
        var seenIds = new HashSet<ShellPolicyCandidateId>();
        var matches = new List<ToolApprovalMatch>();
        foreach (var candidateMatch in result.CandidateMatches)
        {
            if (!expectedIds.Contains(candidateMatch.CandidateId)
                || !seenIds.Add(candidateMatch.CandidateId))
            {
                return false;
            }

            if (candidateMatch.Match is null)
            {
                if (candidateMatch.GrantCoverage is not null
                    || candidateMatch.GrantCreatedAt is not null
                    || candidateMatch.NearMisses.Count > 1
                    || candidateMatch.NearMisses.Any(static nearMiss =>
                        !Enum.IsDefined(nearMiss.Reason)))
                {
                    return false;
                }

                continue;
            }

            if (candidateMatch.GrantCoverage is not
                (ShellCoverageKind.Session
                or ShellCoverageKind.PersistentGlobal
                or ShellCoverageKind.PersistentFolder)
                || candidateMatch.NearMisses.Count > 0
                || (candidateMatch.GrantCoverage == ShellCoverageKind.Session
                    && candidateMatch.GrantCreatedAt is not null))
            {
                return false;
            }

            var candidate = candidates.First(item => item.Id == candidateMatch.CandidateId);
            if (!IsConsistentActorMatch(
                    candidate.Candidate,
                    candidateMatch.Match,
                    candidateMatch.GrantCoverage.Value,
                    cwd))
            {
                return false;
            }

            if (result.PersistentStore is PersistentGrantStoreStatus.Unavailable
                && candidateMatch.GrantCoverage is
                    (ShellCoverageKind.PersistentGlobal or ShellCoverageKind.PersistentFolder))
            {
                return false;
            }

            coverage.Cover(
                candidateMatch.CandidateId,
                candidateMatch.GrantCoverage.Value,
                ToPolicyReason(candidateMatch.GrantCoverage.Value));
            matches.Add(candidateMatch.Match);
        }

        approvalMatches = Array.AsReadOnly(matches.ToArray());
        return true;
    }

    private static bool IsConsistentActorMatch(
        ApprovalCandidate candidate,
        ToolApprovalMatch match,
        ShellCoverageKind coverage,
        string? cwd)
    {
        if (!string.Equals(match.Pattern, candidate.Verb, StringComparison.Ordinal))
            return false;

        if (coverage == ShellCoverageKind.Session)
        {
            return string.Equals(match.Source, "session", StringComparison.Ordinal)
                   && string.Equals(match.Scope, "this chat", StringComparison.Ordinal);
        }

        if (coverage is not
            (ShellCoverageKind.PersistentGlobal or ShellCoverageKind.PersistentFolder)
            || !string.Equals(match.Source, "persistent", StringComparison.Ordinal)
            || !ApprovalEntry.TryParseScope(match.Scope, out var entry, out _)
            || entry.Shell != candidate.Shell
            || entry.Match is null
            || (coverage == ShellCoverageKind.PersistentGlobal) != (entry.Directory is null))
        {
            return false;
        }

        return ApprovalPatternMatching.MatchesShellApproval(candidate, cwd, [entry]);
    }

    private static IReadOnlyList<ShellPolicyCandidate> GetUncoveredCandidates(
        ShellPolicyProjection projection,
        ShellCoverageSet coverage)
    {
        var uncoveredIds = coverage.UncoveredIds.ToHashSet();
        return projection.Candidates
            .Where(candidate => uncoveredIds.Contains(candidate.Id))
            .ToArray();
    }

    private static ShellPolicyReason ToPolicyReason(ShellCoverageKind kind) => kind switch
    {
        ShellCoverageKind.Session => ShellPolicyReason.SessionGrant,
        ShellCoverageKind.PersistentGlobal => ShellPolicyReason.PersistentGlobalGrant,
        ShellCoverageKind.PersistentFolder => ShellPolicyReason.PersistentFolderGrant,
        _ => throw new InvalidOperationException("Invalid actor coverage kind."),
    };

    private static ShellApprovalMatchResult CreateEmptyMatchResult(
        IReadOnlyList<ShellPolicyCandidate> candidates)
        => new(
            new PersistentGrantStoreStatus.Ready(),
            Array.AsReadOnly(candidates
                .Select(static candidate => new ShellGrantCandidateMatch(
                    candidate.Id,
                    Match: null,
                    GrantCoverage: null,
                    NearMisses: []))
                .ToArray()));

    private static ToolAuthorizationDecision CompleteOneTimeOrPrompt(
        string toolName,
        ShellPolicyProjection projection,
        ToolApprovalContext approvalContext,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ShellPolicyDecisionTraceBuilder trace)
    {
        var decision = projection.HasExactOneTimeApproval(toolName, approvalContext)
            ? ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval, approvalMatches)
            : ToolAuthorizationDecision.RequiresApproval(approvalContext, approvalMatches);
        return CompleteWithTrace(decision, trace);
    }

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

    private static ToolAuthorizationDecision Complete(
        ToolAccessDecision decision,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ShellPolicyDecisionTraceBuilder trace)
    {
        if (decision.NeedsApproval)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.RequiresApproval(
                    decision.ApprovalContext
                    ?? throw new InvalidOperationException("Approval decision missing approval context."),
                    approvalMatches),
                trace);
        }

        if (!decision.Allowed)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny(
                    decision.DenyReason
                    ?? throw new InvalidOperationException("Denied decision missing a deny reason.")),
                trace);
        }

        return CompleteWithTrace(
            ToolAuthorizationDecision.Allow(
                decision.AllowReason
                ?? throw new InvalidOperationException("Allowed decision missing an allow reason."),
                approvalMatches),
            trace);
    }

    private static ToolAuthorizationDecision CompleteWithTrace(
        ToolAuthorizationDecision decision,
        ShellPolicyDecisionTraceBuilder trace)
        => decision.WithShellPolicyTrace(trace.Complete(decision));

    private static string FormatApprovalMatches(IReadOnlyList<ToolApprovalMatch> matches)
        => string.Join(", ", matches.Select(match =>
            $"{match.Pattern} [{match.Source}: {match.Scope}]"));

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
