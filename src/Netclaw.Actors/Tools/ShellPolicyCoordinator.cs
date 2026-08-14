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
            return await CompleteStagesAsync(
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
            evaluation.InvalidateStage(ShellPolicyFault.StageException);
            return CompleteEvaluation(evaluation);
        }
    }

    private async Task<ToolAuthorizationDecision> CompleteStagesAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        bool Continue(ShellPolicyStageOutcome outcome)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return evaluation.ApplyStageOutcome(outcome);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Continue(
                ShellPolicyInitialStages.Syntax(evaluation, toolCall.Name))
            || !Continue(
                ShellPolicyInitialStages.ProtectedCausalPaths(evaluation, policy))
            || !Continue(
                ShellPolicyInitialStages.CausalDirectories(evaluation, policy, toolCall.Name)))
        {
            return CompleteEvaluation(evaluation);
        }

        var actorEvidence = await ShellPolicyGrantStages.ActorEvidenceAsync(
            evaluation,
            _approvalEvidence,
            ToApprovalSessionId(context.SessionId),
            context.Audience,
            new ToolName(tool.Name),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Continue(actorEvidence)
            || !Continue(
                ShellPolicyGrantStages.ApprovalExemptSideEffects(
                    evaluation,
                    _approvalEvidence.IsAvailable))
            || !Continue(
                ShellPolicyReviewedSafeStages.RealScope(
                    evaluation,
                    policy,
                    context.Invocation))
            || !Continue(
                ShellPolicyReviewedSafeStages.IntentScope(
                    evaluation,
                    policy,
                    context.Invocation))
            || !Continue(
                ShellPolicyGrantStages.ExactOneTime(
                    evaluation,
                    new ToolName(toolCall.Name),
                    context.SessionDirectory))
            || !Continue(
                ShellPolicyGrantStages.PersistentStoreAvailability(evaluation))
            || !Continue(
                ShellPolicyTerminalStage.Complete(evaluation, context)))
        {
            return CompleteEvaluation(evaluation);
        }

        evaluation.InvalidateStage(ShellPolicyFault.InvalidStageResult);

        return CompleteEvaluation(evaluation);
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

    private static ToolAuthorizationDecision CompleteEvaluation(ShellPolicyEvaluation evaluation)
    {
        var decision = evaluation.TerminalDecision
                       ?? throw new InvalidOperationException("Shell policy stage did not set a decision.");
        var completedTrace = evaluation.CompletedTrace
                             ?? throw new InvalidOperationException("Shell policy stage did not complete its trace.");
        return decision.WithShellPolicyTrace(completedTrace);
    }

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}

internal static class ShellPolicyInitialStages
{
    internal static ShellPolicyStageOutcome Syntax(
        ShellPolicyEvaluation evaluation,
        string toolName)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var projection = evaluation.Projection;
        if (projection.ApprovalContext.IsMessy && !projection.HasCausalIntent)
            return CreateOneTimeOrPrompt(evaluation, toolName);

        if (projection.Candidates.Count == 0)
            return CreateOneTimeOrPrompt(evaluation, toolName);

        var expectedShell = projection.Environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        if (projection.Candidates.Any(static candidate =>
                candidate.Candidate.Shell is null
                || candidate.Candidate.VerbTokens is null))
        {
            return CreateOneTimeOrPrompt(evaluation, toolName);
        }

        if (projection.Candidates.Any(candidate =>
                candidate.Candidate.Shell != expectedShell
                || candidate.Candidate.VerbTokens!.Count == 0
                || candidate.Candidate.VerbTokens.Any(static token =>
                    token.Length == 0 || token.Any(char.IsWhiteSpace))))
        {
            return evaluation.Fault(ShellPolicyFault.InvalidProjection);
        }

        return ShellPolicyStageOutcome.Continue;
    }

    internal static ShellPolicyStageOutcome ProtectedCausalPaths(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(policy);
        return evaluation.Candidates.Any(candidate =>
                   candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                   && policy.CausalIntentReferencesProtectedPath(
                       evaluation.Projection.PathFacts[candidate.Id.Value]))
            ? evaluation.Complete(
                ToolAuthorizationDecision.Deny("shell_references_protected_path"))
            : ShellPolicyStageOutcome.Continue;
    }

    internal static ShellPolicyStageOutcome CausalDirectories(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        string toolName)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return evaluation.Candidates.Any(candidate =>
                   candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                   && candidate.IntentDirectory is { } intentDirectory
                   && !policy.AreCausalIntentDirectoriesEligible(
                       intentDirectory,
                       candidate.IntentFallbackDirectories))
            ? CreateOneTimeOrPrompt(evaluation, toolName)
            : ShellPolicyStageOutcome.Continue;
    }

    private static ShellPolicyStageOutcome CreateOneTimeOrPrompt(
        ShellPolicyEvaluation evaluation,
        string toolName)
    {
        var projection = evaluation.Projection;
        if (!projection.HasExactOneTimeApproval(toolName, projection.ApprovalContext))
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.RequiresApproval(projection.ApprovalContext));
        }

        return evaluation.Complete(
            ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval),
            allowsUncoveredOneTime: true);
    }
}

internal static class ShellPolicyGrantStages
{
    internal static async ValueTask<ShellPolicyStageOutcome> ActorEvidenceAsync(
        ShellPolicyEvaluation evaluation,
        ShellApprovalEvidenceAdapter approvalEvidence,
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(approvalEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName.Value);
        var projection = evaluation.Projection;
        var grantCandidates = projection.GrantCandidates;
        var requestCandidates = grantCandidates
            .Select(candidate => new ShellGrantCandidate(
                candidate.Id,
                candidate.Candidate,
                projection.ApprovalContext.Cwd))
            .ToArray();
        var actorResult = await approvalEvidence.MatchAsync(
            new ShellApprovalMatchRequest(
                sessionId,
                audience,
                toolName,
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
            return evaluation.Fault(ShellPolicyFault.InvalidActorEvidence);
        }

        return evaluation.ApplyActorEvidence(grantEvidence);
    }

    internal static ShellPolicyStageOutcome ApprovalExemptSideEffects(
        ShellPolicyEvaluation evaluation,
        bool approvalEvidenceAvailable)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!approvalEvidenceAvailable)
            return ShellPolicyStageOutcome.Continue;

        foreach (var candidate in evaluation.Candidates.Where(static item =>
                     item.Role == ShellPolicyCandidateRole.Ordinary
                     && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
        {
            var result = evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.ApprovalExemptSideEffect);
            if (result != ShellPolicyStageOutcome.Continue)
                return result;
        }

        return ShellPolicyStageOutcome.Continue;
    }

    internal static ShellPolicyStageOutcome ExactOneTime(
        ShellPolicyEvaluation evaluation,
        ToolName toolName,
        string? sessionDirectory)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName.Value);
        var uncovered = evaluation.UncoveredCandidates;
        if (uncovered.Count == 0)
            return ShellPolicyStageOutcome.Continue;

        var remainingContext = evaluation.GetUncoveredApprovalContext(sessionDirectory);
        if (!evaluation.Projection.HasExactOneTimeApproval(toolName.Value, remainingContext))
            return ShellPolicyStageOutcome.Continue;

        foreach (var candidate in uncovered)
        {
            var result = evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.OneTime);
            if (result != ShellPolicyStageOutcome.Continue)
                return result;
        }

        return ShellPolicyStageOutcome.Continue;
    }

    internal static ShellPolicyStageOutcome PersistentStoreAvailability(
        ShellPolicyEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluation.GrantEvidence is null)
            return evaluation.Fault(ShellPolicyFault.InvalidActorEvidence);

        return evaluation.UncoveredCandidates.Count > 0
               && evaluation.GrantEvidence.PersistentStore
               is PersistentGrantStoreStatus.Unavailable
            ? evaluation.Complete(
                ToolAuthorizationDecision.Deny("approval_store_unavailable"))
            : ShellPolicyStageOutcome.Continue;
    }
}

internal static class ShellPolicyReviewedSafeStages
{
    internal static ShellPolicyStageOutcome RealScope(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanUseReviewedSafePolicy(evaluation))
            return ShellPolicyStageOutcome.Continue;

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

            var result = evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.ReviewedSafeReal);
            if (result != ShellPolicyStageOutcome.Continue)
                return result;
        }

        return ShellPolicyStageOutcome.Continue;
    }

    internal static ShellPolicyStageOutcome IntentScope(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanUseReviewedSafePolicy(evaluation))
            return ShellPolicyStageOutcome.Continue;

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

            var result = evaluation.Cover(
                candidate,
                ShellPolicyCoverageSource.ReviewedSafeIntent);
            if (result != ShellPolicyStageOutcome.Continue)
                return result;
        }

        return ShellPolicyStageOutcome.Continue;
    }

    private static bool CanUseReviewedSafePolicy(ShellPolicyEvaluation evaluation)
        => evaluation.Projection.RunScope.InteractiveApproval
            is InteractiveApprovalCapability.Available;
}

internal static class ShellPolicyTerminalStage
{
    internal static ShellPolicyStageOutcome Complete(
        ShellPolicyEvaluation evaluation,
        ToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(context);
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
}
