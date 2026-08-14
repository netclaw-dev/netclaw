// -----------------------------------------------------------------------
// <copyright file="ShellPolicyReviewedSafeStages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class ShellPolicyReviewedSafeStages
{
    internal static ShellPolicyStage RealScope(
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(invocation);
        return (evaluation, _) => ValueTask.FromResult(
            EvaluateRealScope(evaluation, policy, invocation));
    }

    internal static ShellPolicyStage IntentScope(
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(invocation);
        return (evaluation, _) => ValueTask.FromResult(
            EvaluateIntentScope(evaluation, policy, invocation));
    }

    private static ShellPolicyStageResult EvaluateRealScope(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        if (!CanUseReviewedSafePolicy(evaluation))
            return new ShellPolicyStageResult.Continue();

        foreach (var candidate in evaluation.Projection.GrantCandidates.Where(candidate =>
                     candidate.CanUseRealReviewedSafePolicy
                     && !evaluation.IsCovered(candidate.Id)))
        {
            if (!policy.IsReviewedSafeCandidate(
                    candidate.Candidate,
                    candidate.SourceOccurrence,
                    evaluation.Projection.ApprovalContext.Cwd,
                    invocation))
            {
                continue;
            }

            var result = evaluation.Cover(
                candidate,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ReviewedSafePhrase,
                ShellScopeRelation.UnderRealRoot);
            if (result is not ShellPolicyStageResult.Continue)
                return result;
        }

        return new ShellPolicyStageResult.Continue();
    }

    private static ShellPolicyStageResult EvaluateIntentScope(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        if (!CanUseReviewedSafePolicy(evaluation))
            return new ShellPolicyStageResult.Continue();

        foreach (var candidate in evaluation.Candidates.Where(candidate =>
                     candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
                     && !evaluation.IsCovered(candidate.Id)))
        {
            if (candidate.IntentDirectory is null
                || candidate.IntentPrerequisites.Count == 0
                || candidate.IntentPrerequisites.Any(prerequisite =>
                    !evaluation.IsCovered(prerequisite))
                || !policy.IsReviewedSafeIntentCandidate(
                    candidate.Candidate,
                    candidate.SourceOccurrence,
                    candidate.IntentDirectory,
                    invocation))
            {
                continue;
            }

            var result = evaluation.Cover(
                candidate,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ReviewedSafePhrase,
                ShellScopeRelation.UnderIntentRoot);
            if (result is not ShellPolicyStageResult.Continue)
                return result;
        }

        return new ShellPolicyStageResult.Continue();
    }

    private static bool CanUseReviewedSafePolicy(ShellPolicyEvaluation evaluation)
        => evaluation.Projection.RunScope.InteractiveApproval
            is InteractiveApprovalCapability.Available;
}
