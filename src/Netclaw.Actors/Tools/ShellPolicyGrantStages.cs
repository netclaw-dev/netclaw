// -----------------------------------------------------------------------
// <copyright file="ShellPolicyGrantStages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class ShellPolicyGrantStages
{
    internal static ShellPolicyStage ActorEvidence(
        ShellApprovalEvidenceAdapter approvalEvidence,
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName)
    {
        ArgumentNullException.ThrowIfNull(approvalEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName.Value);
        return (evaluation, cancellationToken) => EvaluateActorEvidenceAsync(
            evaluation,
            approvalEvidence,
            sessionId,
            audience,
            toolName,
            cancellationToken);
    }

    internal static ShellPolicyStage ApprovalExemptSideEffects(bool approvalEvidenceAvailable)
        => (evaluation, _) => ValueTask.FromResult(
            EvaluateApprovalExemptSideEffects(evaluation, approvalEvidenceAvailable));

    internal static ShellPolicyStage ExactOneTime(
        ToolName toolName,
        string? sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName.Value);
        return (evaluation, _) => ValueTask.FromResult(
            EvaluateExactOneTime(evaluation, toolName, sessionDirectory));
    }

    internal static ShellPolicyStage PersistentStoreAvailability()
        => static (evaluation, _) => ValueTask.FromResult(
            EvaluatePersistentStoreAvailability(evaluation));

    private static async ValueTask<ShellPolicyStageResult> EvaluateActorEvidenceAsync(
        ShellPolicyEvaluation evaluation,
        ShellApprovalEvidenceAdapter approvalEvidence,
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        CancellationToken cancellationToken)
    {
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
            return new ShellPolicyStageResult.Fault(ShellPolicyFault.InvalidActorEvidence);
        }

        return evaluation.ApplyActorEvidence(grantEvidence);
    }

    private static ShellPolicyStageResult EvaluateApprovalExemptSideEffects(
        ShellPolicyEvaluation evaluation,
        bool approvalEvidenceAvailable)
    {
        if (!approvalEvidenceAvailable)
            return new ShellPolicyStageResult.Continue();

        foreach (var candidate in evaluation.Candidates.Where(static item =>
                     item.Role == ShellPolicyCandidateRole.Ordinary
                     && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
        {
            var result = evaluation.Cover(
                candidate,
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ApprovalExemptSideEffect,
                ShellScopeRelation.None);
            if (result is not ShellPolicyStageResult.Continue)
                return result;
        }

        return new ShellPolicyStageResult.Continue();
    }

    private static ShellPolicyStageResult EvaluateExactOneTime(
        ShellPolicyEvaluation evaluation,
        ToolName toolName,
        string? sessionDirectory)
    {
        var uncovered = evaluation.UncoveredCandidates;
        if (uncovered.Count == 0)
            return new ShellPolicyStageResult.Continue();

        var remainingContext = evaluation.GetUncoveredApprovalContext(sessionDirectory);
        if (!evaluation.Projection.HasExactOneTimeApproval(toolName.Value, remainingContext))
            return new ShellPolicyStageResult.Continue();

        foreach (var candidate in uncovered)
        {
            var result = evaluation.Cover(
                candidate,
                ShellCoverageKind.OneTime,
                ShellPolicyReason.OneTimeGrant,
                ShellScopeRelation.None);
            if (result is not ShellPolicyStageResult.Continue)
                return result;
        }

        return new ShellPolicyStageResult.Continue();
    }

    private static ShellPolicyStageResult EvaluatePersistentStoreAvailability(
        ShellPolicyEvaluation evaluation)
    {
        if (evaluation.GrantEvidence is null)
            return new ShellPolicyStageResult.Fault(ShellPolicyFault.InvalidActorEvidence);

        return evaluation.UncoveredCandidates.Count > 0
               && evaluation.GrantEvidence.PersistentStore
               is PersistentGrantStoreStatus.Unavailable
            ? new ShellPolicyStageResult.Complete(
                ToolAuthorizationDecision.Deny("approval_store_unavailable"))
            : new ShellPolicyStageResult.Continue();
    }
}
