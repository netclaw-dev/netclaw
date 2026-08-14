// -----------------------------------------------------------------------
// <copyright file="ShellPolicyInitialStages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class ShellPolicyInitialStages
{
    internal static ShellPolicyStage Syntax(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return (evaluation, _) => ValueTask.FromResult(EvaluateSyntax(evaluation, toolName));
    }

    internal static ShellPolicyStage ProtectedCausalPaths(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return (evaluation, _) => ValueTask.FromResult(EvaluateProtectedCausalPaths(evaluation, policy));
    }

    internal static ShellPolicyStage CausalDirectories(
        ToolAccessPolicy policy,
        string toolName)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return (evaluation, _) => ValueTask.FromResult(
            EvaluateCausalDirectories(evaluation, policy, toolName));
    }

    private static ShellPolicyStageResult EvaluateSyntax(
        ShellPolicyEvaluation evaluation,
        string toolName)
    {
        var projection = evaluation.Projection;
        if (projection.ApprovalContext.IsMessy && !projection.HasCausalIntent)
            return CreateOneTimeOrPrompt(projection, toolName);

        if (projection.Candidates.Count == 0)
            return CreateOneTimeOrPrompt(projection, toolName);

        var expectedShell = projection.Environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        if (projection.Candidates.Any(static candidate =>
                candidate.Candidate.Shell is null
                || candidate.Candidate.VerbTokens is null))
        {
            return CreateOneTimeOrPrompt(projection, toolName);
        }

        if (projection.Candidates.Any(candidate =>
                candidate.Candidate.Shell != expectedShell
                || candidate.Candidate.VerbTokens!.Count == 0
                || candidate.Candidate.VerbTokens.Any(static token =>
                    token.Length == 0 || token.Any(char.IsWhiteSpace))))
        {
            return new ShellPolicyStageResult.Fault(ShellPolicyFault.InvalidProjection);
        }

        return new ShellPolicyStageResult.Continue();
    }

    private static ShellPolicyStageResult EvaluateProtectedCausalPaths(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy)
        => evaluation.Candidates.Any(candidate =>
            candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
            && policy.CausalIntentReferencesProtectedPath(
                evaluation.Projection.PathFacts.For(candidate.Id)))
            ? new ShellPolicyStageResult.Complete(
                ToolAuthorizationDecision.Deny("shell_references_protected_path"))
            : new ShellPolicyStageResult.Continue();

    private static ShellPolicyStageResult EvaluateCausalDirectories(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        string toolName)
        => evaluation.Candidates.Any(candidate =>
            candidate.Role == ShellPolicyCandidateRole.CausalIntentConsumer
            && candidate.IntentDirectory is { } intentDirectory
            && !policy.AreCausalIntentDirectoriesEligible(
                intentDirectory,
                candidate.IntentFallbackDirectories))
            ? CreateOneTimeOrPrompt(evaluation.Projection, toolName)
            : new ShellPolicyStageResult.Continue();

    private static ShellPolicyStageResult CreateOneTimeOrPrompt(
        ShellPolicyProjection projection,
        string toolName)
        => projection.HasExactOneTimeApproval(toolName, projection.ApprovalContext)
            ? ShellPolicyStageResult.Complete.ExactOneTime(
                ToolAuthorizationDecision.Allow(ToolAllowReason.OneTimeApproval))
            : new ShellPolicyStageResult.Complete(
                ToolAuthorizationDecision.RequiresApproval(projection.ApprovalContext));
}
