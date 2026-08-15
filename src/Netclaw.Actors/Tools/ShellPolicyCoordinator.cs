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
        CancellationToken cancellationToken)
    {
        var evaluation = new ShellPolicyEvaluation(projection);
        try
        {
            return await EvaluatePolicyAsync(
                tool,
                toolCall,
                context,
                evaluation,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return evaluation.InternalFailure();
        }
    }

    private async Task<ToolAuthorizationDecision> EvaluatePolicyAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projection = evaluation.Projection;
        if (projection.ApprovalContext.IsMessy && !projection.HasCausalIntent
            || projection.Candidates.Count == 0
            || projection.Candidates.Any(static candidate =>
                candidate.Candidate.Shell is null
                || candidate.Candidate.VerbTokens is null))
        {
            return CompleteOneTimeOrPrompt(evaluation, toolCall.Name);
        }

        var expectedShell = projection.Environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        if (projection.Candidates.Any(candidate =>
                candidate.Candidate.Shell != expectedShell
                || candidate.Candidate.VerbTokens!.Count == 0
                || candidate.Candidate.VerbTokens.Any(static token =>
                    token.Length == 0 || token.Any(char.IsWhiteSpace))))
        {
            throw new InvalidOperationException("Invalid shell policy projection.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (projection.Candidates.Any(candidate =>
                candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                && policy.CausalIntentReferencesProtectedPath(
                    projection.PathFacts[candidate.Id.Value])))
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.Deny("shell_references_protected_path"));
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (projection.Candidates.Any(candidate =>
                candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                && candidate.IntentDirectory is { } intentDirectory
                && !policy.AreCausalIntentDirectoriesEligible(
                    intentDirectory,
                    candidate.IntentFallbackDirectories)))
        {
            return CompleteOneTimeOrPrompt(evaluation, toolCall.Name);
        }
        cancellationToken.ThrowIfCancellationRequested();

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
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidatedShellGrantEvidence.TryCreate(
                actorResult,
                grantCandidates,
                projection.ApprovalContext.Cwd,
                out var grantEvidence)
            || grantEvidence is null)
        {
            throw new InvalidOperationException("Invalid shell approval evidence.");
        }

        evaluation.ApplyActorEvidence(grantEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        if (_approvalEvidence.IsAvailable)
        {
            foreach (var candidate in evaluation.Candidates.Where(static item =>
                         item.Role == ShellPolicyCandidateRole.Ordinary
                         && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
            {
                evaluation.Cover(
                    candidate,
                    ShellPolicyCoverageSource.ApprovalExemptSideEffect);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (projection.RunScope.InteractiveApproval
            is InteractiveApprovalCapability.Available)
        {
            ApplyReviewedSafeCoverage(evaluation, policy, context.Invocation);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var uncovered = evaluation.UncoveredCandidates;
        if (uncovered.Count > 0)
        {
            var remainingContext = evaluation.GetUncoveredApprovalContext(
                context.SessionDirectory);
            if (projection.HasExactOneTimeApproval(toolCall.Name, remainingContext))
            {
                foreach (var candidate in uncovered)
                    evaluation.Cover(candidate, ShellPolicyCoverageSource.OneTime);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (evaluation.UncoveredCandidates.Count > 0
            && evaluation.GrantEvidence?.PersistentStore
            is PersistentGrantStoreStatus.Unavailable)
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.Deny("approval_store_unavailable"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CompleteFinal(evaluation, context);
    }

    private static void ApplyReviewedSafeCoverage(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        foreach (var candidate in evaluation.Projection.GrantCandidates.Where(candidate =>
                     candidate.CanUseRealReviewedSafePolicy
                     && !evaluation.IsCovered(candidate.Id)))
        {
            if (!policy.IsReviewedSafeCandidate(
                    candidate,
                    evaluation.Projection.PathFacts[candidate.Id.Value],
                    invocation))
            {
                continue;
            }

            evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.ReviewedSafeReal);
        }

        foreach (var candidate in evaluation.Candidates.Where(candidate =>
                     candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                     && !evaluation.IsCovered(candidate.Id)))
        {
            if (candidate.IntentDirectory is null
                || candidate.IntentPrerequisites.Count == 0
                || candidate.IntentPrerequisites.Any(prerequisite =>
                    !evaluation.IsCovered(prerequisite))
                || !policy.IsReviewedSafeIntentCandidate(
                    candidate,
                    evaluation.Projection.PathFacts[candidate.Id.Value],
                    invocation))
            {
                continue;
            }

            evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.ReviewedSafeIntent);
        }
    }

    private static ToolAuthorizationDecision CompleteFinal(
        ShellPolicyEvaluation evaluation,
        ToolExecutionContext context)
    {
        var projection = evaluation.Projection;
        var approvalMatches = evaluation.ApprovalMatches;
        var uncovered = evaluation.UncoveredCandidates;
        if (uncovered.Count > 0)
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.RequiresApproval(
                    evaluation.GetUncoveredApprovalContext(context.SessionDirectory),
                    approvalMatches));
        }

        if (!evaluation.AllCovered)
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.Deny("internal_policy_failure"));
        }

        if (evaluation.HasOneTimeCoverage)
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.OneTimeApproval,
                    approvalMatches));
        }

        var grantCandidates = projection.GrantCandidates;
        if (approvalMatches.Count > 0)
        {
            if (approvalMatches.Count == grantCandidates.Count)
            {
                context.Approval.ApplyDecision(
                    "PreviouslyApproved",
                    FormatApprovalMatches(approvalMatches));
            }

            return evaluation.Complete(
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.StoredApproval,
                    approvalMatches));
        }

        return evaluation.Complete(
            ToolAuthorizationDecision.Allow(
                grantCandidates.Count == 0
                    ? ToolAllowReason.ApprovalExemptShellCandidates
                    : ToolAllowReason.SafeVerbInTrustedScope));
    }

    private static string FormatApprovalMatches(IReadOnlyList<ToolApprovalMatch> matches)
        => string.Join(", ", matches.Select(match =>
            $"{match.Pattern} [{match.Source}: {match.Scope}]"));

    private static ToolAuthorizationDecision CompleteOneTimeOrPrompt(
        ShellPolicyEvaluation evaluation,
        string toolName)
    {
        var projection = evaluation.Projection;
        return projection.HasExactOneTimeApproval(toolName, projection.ApprovalContext)
            ? evaluation.Complete(
                ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval),
                allowsUncoveredOneTime: true)
            : evaluation.Complete(
                ToolAuthorizationDecision.RequiresApproval(projection.ApprovalContext));
    }

    private static ToolAuthorizationDecision Complete(
        ToolAccessDecision decision,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ShellPolicyDecisionTraceBuilder trace)
        => CompleteWithTrace(
            ToolAuthorizationDecision.From(decision, approvalMatches),
            trace);

    private static ToolAuthorizationDecision CompleteWithTrace(
        ToolAuthorizationDecision decision,
        ShellPolicyDecisionTraceBuilder trace)
        => decision.WithShellPolicyTrace(trace.Complete(decision));

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
