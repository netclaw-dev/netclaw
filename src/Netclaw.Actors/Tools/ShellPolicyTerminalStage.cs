// -----------------------------------------------------------------------
// <copyright file="ShellPolicyTerminalStage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class ShellPolicyTerminalStage
{
    internal static ShellPolicyStage Complete(ToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (evaluation, _) => ValueTask.FromResult(
            CompleteEvaluation(evaluation, context));
    }

    private static ShellPolicyStageResult CompleteEvaluation(
        ShellPolicyEvaluation evaluation,
        ToolExecutionContext context)
    {
        var projection = evaluation.Projection;
        var approvalMatches = evaluation.ApprovalMatches;
        var uncovered = evaluation.UncoveredCandidates;
        if (uncovered.Count > 0)
        {
            var promptContext = projection.HasCausalIntent
                ? projection.ApprovalContext
                : ToolAccessPolicy.NarrowShellApprovalContext(
                    projection.ApprovalContext,
                    uncovered.Select(static candidate => candidate.Candidate).ToArray(),
                    context.SessionDirectory,
                    projection.Environment.PathStyle);
            return evaluation.Complete(
                ToolAuthorizationDecision.RequiresApproval(promptContext, approvalMatches));
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
