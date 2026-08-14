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
                projection,
                evaluation,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            evaluation.InvalidateStage(ShellPolicyFault.StageException);
            return CompleteEvaluation(evaluation);
        }
    }

    private async Task<ToolAuthorizationDecision> CompleteStagesAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyProjection projection,
        ShellPolicyEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var initialResult = await ShellPolicyPipeline.RunAsync(
            evaluation,
            [
                ShellPolicyInitialStages.Syntax(toolCall.Name),
                ShellPolicyInitialStages.ProtectedCausalPaths(policy),
                ShellPolicyInitialStages.CausalDirectories(policy, toolCall.Name),
                ShellPolicyGrantStages.ActorEvidence(
                    _approvalEvidence,
                    ToApprovalSessionId(context.SessionId),
                    context.Audience,
                    new ToolName(tool.Name)),
                ShellPolicyGrantStages.ApprovalExemptSideEffects(_approvalEvidence.IsAvailable),
                ShellPolicyReviewedSafeStages.RealScope(policy, context.Invocation),
                ShellPolicyReviewedSafeStages.IntentScope(policy, context.Invocation)
            ],
            cancellationToken);
        if (initialResult is not ShellPolicyStageResult.Continue)
            return CompleteEvaluation(evaluation);

        var grantEvidence = evaluation.GrantEvidence;
        if (grantEvidence is null)
        {
            evaluation.Fault(ShellPolicyFault.InvalidActorEvidence);
            return CompleteEvaluation(evaluation);
        }

        var grantCandidates = projection.GrantCandidates;
        var approvalMatches = evaluation.ApprovalMatches;

        var uncovered = evaluation.UncoveredCandidates;
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
                    var coverageResult = evaluation.Cover(
                        candidate,
                        ShellCoverageKind.OneTime,
                        ShellPolicyReason.OneTimeGrant,
                        ShellScopeRelation.None);
                    if (coverageResult is not ShellPolicyStageResult.Continue)
                        return CompleteEvaluation(evaluation);
                }

                uncovered = [];
                oneTimeApplied = true;
            }
        }

        if (uncovered.Count > 0
            && grantEvidence.PersistentStore is PersistentGrantStoreStatus.Unavailable)
        {
            return CompleteEvaluation(
                evaluation,
                ToolAuthorizationDecision.Deny("approval_store_unavailable"));
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
            return CompleteEvaluation(
                evaluation,
                ToolAuthorizationDecision.RequiresApproval(promptContext, approvalMatches));
        }

        if (!evaluation.AllCovered)
        {
            return CompleteEvaluation(
                evaluation,
                ToolAuthorizationDecision.Deny("internal_policy_failure"));
        }

        if (oneTimeApplied)
        {
            return CompleteEvaluation(
                evaluation,
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.OneTimeApproval,
                    approvalMatches));
        }

        if (approvalMatches.Count > 0)
        {
            if (approvalMatches.Count == grantCandidates.Count)
            {
                context.Approval.ApplyDecision(
                    "PreviouslyApproved",
                    FormatApprovalMatches(approvalMatches));
            }

            return CompleteEvaluation(
                evaluation,
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.StoredApproval,
                    approvalMatches));
        }

        return CompleteEvaluation(
            evaluation,
            ToolAuthorizationDecision.Allow(
                grantCandidates.Count == 0
                    ? ToolAllowReason.ApprovalExemptShellCandidates
                    : ToolAllowReason.SafeVerbInTrustedScope));
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

    private static ToolAuthorizationDecision CompleteEvaluation(ShellPolicyEvaluation evaluation)
    {
        var decision = evaluation.TerminalDecision
                       ?? throw new InvalidOperationException("Shell policy stage did not set a decision.");
        var completedTrace = evaluation.CompletedTrace
                             ?? throw new InvalidOperationException("Shell policy stage did not complete its trace.");
        return decision.WithShellPolicyTrace(completedTrace);
    }

    private static ToolAuthorizationDecision CompleteEvaluation(
        ShellPolicyEvaluation evaluation,
        ToolAuthorizationDecision decision)
    {
        evaluation.Complete(decision);
        return CompleteEvaluation(evaluation);
    }

    private static string FormatApprovalMatches(IReadOnlyList<ToolApprovalMatch> matches)
        => string.Join(", ", matches.Select(match =>
            $"{match.Pattern} [{match.Source}: {match.Scope}]"));

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
