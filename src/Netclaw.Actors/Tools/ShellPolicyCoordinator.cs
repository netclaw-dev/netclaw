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
        var result = await ShellPolicyPipeline.RunAsync(
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
                ShellPolicyReviewedSafeStages.IntentScope(policy, context.Invocation),
                ShellPolicyGrantStages.ExactOneTime(
                    new ToolName(toolCall.Name),
                    context.SessionDirectory),
                ShellPolicyGrantStages.PersistentStoreAvailability(),
                ShellPolicyTerminalStage.Complete(context)
            ],
            cancellationToken);
        if (result is ShellPolicyStageResult.Continue)
            evaluation.Fault(ShellPolicyFault.InvalidStageResult);

        return CompleteEvaluation(evaluation);
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

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
