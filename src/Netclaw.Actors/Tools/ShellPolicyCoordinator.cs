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
/// Coordinates shell preflight, correction selection, one approval-store check, and final policy.
/// </summary>
internal sealed class ShellPolicyCoordinator(
    ToolRegistry registry,
    ToolAccessPolicy policy,
    IToolApprovalService? approvalService)
{
    private readonly ShellApprovalEvidenceAdapter _approvalEvidence = new(approvalService);

    /// <summary>Evaluates one shell request from access checks through its final authorization result.</summary>
    /// <remarks>
    /// The access policy creates one canonical command analysis and applies hard denials first.
    /// The coordinator then collects corrections before it accepts automatic policy approval or checks stored approval evidence.
    /// It returns the analysis only when the caller can start the authorized command.
    /// </remarks>
    internal async Task<(ToolAuthorizationDecision Decision, ShellCommandAnalysis? AuthorizedAnalysis)> EvaluateAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var trace = new ShellPolicyDecisionTraceBuilder();
        try
        {
            var preflight = policy.AuthorizeShellPreflight(
                tool,
                context,
                toolCall.Arguments);
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
            return (
                CompleteWithTrace(
                    ToolAuthorizationDecision.Deny("internal_policy_failure"),
                    trace),
                null);
        }
    }

    private async Task<(ToolAuthorizationDecision Decision, ShellCommandAnalysis? AuthorizedAnalysis)> EvaluateCoreAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight,
        ShellPolicyDecisionTraceBuilder trace,
        CancellationToken cancellationToken)
    {
        var analysis = preflight switch
        {
            ShellPolicyPreflightResult.Complete preflightComplete => preflightComplete.AuthorizedAnalysis,
            ShellPolicyPreflightResult.Continue preflightContinuation => preflightContinuation.Analysis,
            _ => throw new InvalidOperationException("Unsupported shell policy preflight result."),
        };
        cancellationToken.ThrowIfCancellationRequested();
        var corrections = analysis is null
            ? null
            : CollectApplicableCorrections(analysis, toolCall, context, preflight);
        cancellationToken.ThrowIfCancellationRequested();

        if (corrections is not null
            && (corrections.Items.Any(static correction => correction is ToolCorrection.NativeToolSuggested)
                || preflight is ShellPolicyPreflightResult.Complete
                {
                    Decision.AllowReason: ToolAllowReason.PolicyAuto
                }))
        {
            return (
                Complete(ToolAuthorizationDecision.RequireAgentCorrection(corrections), [], trace),
                null);
        }

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
                preflightDecision = ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval);
            }

            return (
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
                policy.IsEligiblePlatformTemporaryPath,
                out var projection)
            || projection is null)
        {
            return (
                CompleteWithTrace(
                    ToolAuthorizationDecision.Deny("internal_policy_failure"),
                    trace),
                null);
        }

        var projectedPathDecision = policy.EnforceProjectedShellFileProtection(
            projection.PathFacts,
            context.Invocation);
        if (projectedPathDecision is not null)
        {
            return (
                CompleteWithTrace(projectedPathDecision, trace),
                null);
        }

        var decision = await CompleteAsync(
            tool,
            toolCall,
            context,
            projection,
            corrections,
            cancellationToken);

        return (
            decision,
            decision.Outcome == ToolAuthorizationOutcome.Allowed
                ? continuation.Analysis
                : null);
    }

    /// <summary>Collects compatible advice from the same invocation and its existing policies.</summary>
    internal ToolCorrectionCollection? CollectApplicableCorrections(
        ShellCommandAnalysis analysis,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight)
    {
        if (preflight is ShellPolicyPreflightResult.Complete { Decision.Allowed: false })
            return null;

        var native = NativeToolShellCorrectionDetector.Detect(analysis, registry, policy, context.Invocation);
        var applicable = new List<ToolCorrection>();
        if (native is not null)
            applicable.Add(native.Correction);

        // A native operation without relocation support must keep its target. Retry keys never authorize replacement native calls.
        if (native is { SupportsManagedTemporaryDirectory: false }
            || native is null && context.Approval.ManagedTemporaryRetry is not null)
        {
            return applicable.Count == 0 ? null : new ToolCorrectionCollection(applicable);
        }

        IReadOnlyList<ApprovalCandidate> candidates;
        bool isMessy;
        if (preflight is ShellPolicyPreflightResult.Continue continuation)
        {
            candidates = continuation.ApprovalContext.Candidates!;
            isMessy = continuation.ApprovalContext.IsMessy;
        }
        else
        {
            // Auto omits the approval context. Reuse its canonical parse without asking the approval store.
            var approval = policy.ShellApprovalMatcher.AnalyzeInvocation(
                new ToolName(toolCall.Name),
                ToolAccessPolicy.WithResolvedShellWorkingDirectory(toolCall.Arguments, analysis.WorkingDirectory),
                analysis);
            candidates = approval.Candidates;
            isMessy = approval.IsMessy;
        }

        var temporary = policy.EvaluateShellTemporaryCorrection(analysis, candidates, toolCall.Arguments, context.Invocation);
        if (temporary is not null)
            applicable.Add(temporary);

        // Relocation invalidates project advice for the original directory. A native replacement also requires fresh policy.
        if (native is null && temporary is null && !isMessy
            && policy.EvaluateShellProjectCorrection(candidates, analysis.WorkingDirectory, context.Invocation) is { } project
            && registry.GetByName(SetWorkingDirectoryTool.ToolName) is SetWorkingDirectoryTool declaration
            && policy.IsToolExposed(declaration, context.Invocation)
            && declaration.CanDeclare(project.Directory, context.Invocation))
        {
            applicable.Add(project);
        }

        return applicable.Count == 0 ? null : new ToolCorrectionCollection(applicable);
    }

    private async Task<ToolAuthorizationDecision> CompleteAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyProjection projection,
        ToolCorrectionCollection? corrections,
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
                corrections,
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
        ToolCorrectionCollection? corrections,
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
            return CompleteOneTimeOrPrompt(evaluation, toolCall.Name, corrections);
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
            return CompleteOneTimeOrPrompt(evaluation, toolCall.Name, corrections);
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
                ToolAccessPolicy.GetSessionOwnedApprovalDirectories(context));
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
        return CompleteFinal(evaluation, context, corrections);
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
        ToolExecutionContext context,
        ToolCorrectionCollection? corrections)
    {
        var projection = evaluation.Projection;
        var approvalMatches = evaluation.ApprovalMatches;
        var uncovered = evaluation.UncoveredCandidates;
        if (uncovered.Count > 0)
        {
            return CompleteApprovalOrCorrection(
                evaluation,
                evaluation.GetUncoveredApprovalContext(
                    ToolAccessPolicy.GetSessionOwnedApprovalDirectories(context)),
                approvalMatches,
                corrections);
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
                    : ToolAllowReason.ReviewedSafePolicy));
    }

    private static string FormatApprovalMatches(IReadOnlyList<ToolApprovalMatch> matches)
        => string.Join(", ", matches.Select(match =>
            $"{match.Pattern} [{match.Source}: {match.Scope}]"));

    private static ToolAuthorizationDecision CompleteOneTimeOrPrompt(
        ShellPolicyEvaluation evaluation,
        string toolName,
        ToolCorrectionCollection? corrections)
    {
        var projection = evaluation.Projection;
        return projection.HasExactOneTimeApproval(toolName, projection.ApprovalContext)
            ? evaluation.Complete(
                ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval),
                allowsUncoveredOneTime: true)
            : CompleteApprovalOrCorrection(
                evaluation,
                projection.ApprovalContext,
                [],
                corrections);
    }

    private static ToolAuthorizationDecision CompleteApprovalOrCorrection(
        ShellPolicyEvaluation evaluation,
        ToolApprovalContext approvalContext,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ToolCorrectionCollection? corrections)
    {
        var decision = corrections is not null
            ? ToolAuthorizationDecision.RequireAgentCorrection(corrections, approvalMatches)
            : ToolAuthorizationDecision.RequiresApproval(approvalContext, approvalMatches);
        return evaluation.Complete(decision);
    }

    private static ToolAuthorizationDecision Complete(
        ToolAuthorizationDecision decision,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ShellPolicyDecisionTraceBuilder trace)
        => CompleteWithTrace(decision.WithApprovalMatches(approvalMatches), trace);

    private static ToolAuthorizationDecision CompleteWithTrace(
        ToolAuthorizationDecision decision,
        ShellPolicyDecisionTraceBuilder trace)
        => decision.WithShellPolicyTrace(trace.Complete(decision));

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
