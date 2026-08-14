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
    private readonly ShellApprovalEvidenceAdapter _approvalEvidence = new(approvalService);

    internal async Task<ShellPolicyAuthorization> EvaluateAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        var trace = new ShellPolicyDecisionTraceBuilder();
        try
        {
            return await EvaluateCoreAsync(
                tool,
                toolCall,
                context,
                preflight,
                trace,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ShellPolicyAuthorization(
                CompleteWithTrace(
                    ToolAuthorizationDecision.Deny("internal_policy_failure"),
                    trace),
                authorizedAnalysis: null);
        }
    }

    private async Task<ShellPolicyAuthorization> EvaluateCoreAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight,
        ShellPolicyDecisionTraceBuilder trace,
        CancellationToken cancellationToken)
    {
        if (preflight is ShellPolicyPreflightResult.Complete complete)
        {
            var preflightDecision = complete.Decision;
            if (preflightDecision.NeedsApproval
                && preflightDecision.ApprovalContext is { } approvalContext
                && OneTimeApprovalKeys.Matches(
                    context.Approval.OneTimeApprovedToolName,
                    context.Approval.OneTimeApprovedPatterns,
                    toolCall.Name,
                    approvalContext))
            {
                preflightDecision = ToolAccessDecision.Allow(ToolAllowReason.OneTimeApproval);
            }

            return new ShellPolicyAuthorization(
                Complete(preflightDecision, [], trace),
                complete.AuthorizedAnalysis);
        }

        if (preflight is not ShellPolicyPreflightResult.Continue continuation
            || !ShellPolicyProjection.TryCreate(
                continuation.Environment,
                policy.ShellApprovalMatcher,
                continuation.Analysis,
                continuation.ApprovalContext,
                context,
                policy.IsSafePlatformTemporaryPath,
                out var projection)
            || projection is null)
        {
            return new ShellPolicyAuthorization(
                CompleteWithTrace(
                    ToolAuthorizationDecision.Deny("internal_policy_failure"),
                    trace),
                authorizedAnalysis: null);
        }

        var decision = await CompleteAsync(
            tool,
            toolCall,
            context,
            projection,
            trace,
            cancellationToken);
        return new ShellPolicyAuthorization(
            decision,
            decision.Outcome == ToolAuthorizationOutcome.Allowed
                ? continuation.Analysis
                : null);
    }

    internal static ShellPolicyAuthorization CompleteInternalFailure()
    {
        var trace = new ShellPolicyDecisionTraceBuilder();
        return new ShellPolicyAuthorization(
            CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace),
            authorizedAnalysis: null);
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
                     _approvalEvidence.IsAvailable
                     && item.Role == ShellPolicyCandidateRole.Ordinary
                     && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
        {
            coverage.Cover(
                candidate.Id,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ApprovalExemptSideEffect);
        }

        var grantCandidates = projection.GrantCandidates;
        var requestCandidates = grantCandidates
            .Select(candidate => new ShellGrantCandidate(
                candidate.Id,
                candidate.Candidate,
                projection.ApprovalContext.Cwd))
            .ToArray();
        var actorResult = await _approvalEvidence.MatchAsync(
            new ShellApprovalMatchRequest(
                ToApprovalSessionId(context.SessionId),
                context.Audience,
                new ToolName(tool.Name),
                projection.Environment,
                Array.AsReadOnly(requestCandidates)),
            projection.ApprovalContext.Cwd,
            cancellationToken);
        if (!ValidatedShellGrantEvidence.TryCreate(
                actorResult,
                grantCandidates,
                projection.ApprovalContext.Cwd,
                out var grantEvidence)
            || grantEvidence is null)
        {
            return CompleteWithTrace(
                ToolAuthorizationDecision.Deny("internal_policy_failure"),
                trace);
        }

        foreach (var candidateEvidence in grantEvidence.CandidateEvidence)
        {
            var actorMatch = candidateEvidence.ActorEvidence;
            if (actorMatch.GrantCoverage is { } grantCoverage)
            {
                coverage.Cover(
                    candidateEvidence.Candidate.Id,
                    grantCoverage,
                    ToPolicyReason(grantCoverage));
            }

            trace.AddActorEvidence(candidateEvidence.Candidate, actorMatch);
        }
        var approvalMatches = grantEvidence.ApprovalMatches;

        foreach (var candidate in projection.Candidates.Where(item =>
                     _approvalEvidence.IsAvailable
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
            && grantEvidence.PersistentStore is PersistentGrantStoreStatus.Unavailable)
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
