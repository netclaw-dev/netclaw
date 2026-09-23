// -----------------------------------------------------------------------
// <copyright file="ShellPolicyCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

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
    internal async Task<ToolAuthorizationResult> EvaluateAsync(
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
            return ToolAuthorizationResult.Stop(
                CompleteWithTrace(
                    ToolAuthorizationDecision.Deny("internal_policy_failure"),
                    trace));
        }
    }

    private async Task<ToolAuthorizationResult> EvaluateCoreAsync(
        INetclawTool tool,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight,
        ShellPolicyDecisionTraceBuilder trace,
        CancellationToken cancellationToken)
    {
        var analysis = preflight switch
        {
            ShellPolicyPreflightResult.Complete
            { Result: ToolAuthorizationResult.ShellExecution execution } => execution.Analysis,
            ShellPolicyPreflightResult.Continue preflightContinuation => preflightContinuation.Analysis,
            _ => null,
        };
        cancellationToken.ThrowIfCancellationRequested();
        var corrections = analysis is null
            ? null
            : CollectApplicableCorrections(analysis, toolCall, context, preflight);
        cancellationToken.ThrowIfCancellationRequested();

        // A native tool needs a separate call. Shell approval cannot authorize that replacement.
        if (corrections?.Items.Any(static correction => correction is ToolCorrection.NativeToolSuggested) == true)
        {
            return ToolAuthorizationResult.Stop(
                Complete(ToolAuthorizationDecision.RequireAgentCorrection(corrections), [], trace));
        }

        if (preflight is ShellPolicyPreflightResult.Complete complete)
        {
            var preflightDecision = complete.Result.Decision;
            // Auto permits execution, but the agent must first receive any applicable directory advice.
            if (preflightDecision.AllowReason == ToolAllowReason.PolicyAuto && corrections is not null)
            {
                return ToolAuthorizationResult.Stop(
                    Complete(ToolAuthorizationDecision.RequireAgentCorrection(corrections), [], trace));
            }

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

            return ToolAuthorizationResult.CreateShell(
                Complete(preflightDecision, [], trace),
                analysis);
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
            return ToolAuthorizationResult.Stop(
                CompleteWithTrace(
                    ToolAuthorizationDecision.Deny("internal_policy_failure"),
                    trace));
        }

        var projectedPathDecision = policy.EnforceProjectedShellFileProtection(
            projection.PathFacts,
            context.Invocation);
        if (projectedPathDecision is not null)
        {
            return ToolAuthorizationResult.Stop(
                CompleteWithTrace(projectedPathDecision, trace));
        }

        var decision = await EvaluatePolicyAsync(
            tool,
            toolCall,
            context,
            projection,
            corrections,
            cancellationToken);

        return ToolAuthorizationResult.CreateShell(
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
        // Denial and approval without command analysis cannot become advice to submit a different call.
        if (preflight is ShellPolicyPreflightResult.Complete
            { Result.Decision.Outcome: not ToolAuthorizationOutcome.Allowed })
            return null;

        var native = NativeToolShellCorrectionDetector.Detect(analysis, registry, policy, context.Invocation);
        var applicable = new List<ToolCorrection>();
        if (native is not null)
            applicable.Add(native.Correction);

        var directory = SelectDirectoryCorrection(native, analysis, toolCall, context, preflight);
        if (directory is not null)
            applicable.Add(directory);

        return applicable.Count == 0 ? null : new ToolCorrectionCollection(applicable);
    }

    private ToolCorrection? SelectDirectoryCorrection(
        NativeToolShellCorrection? native,
        ShellCommandAnalysis analysis,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight)
    {
        // For example, file_read must keep its source path; file_write can create output in the managed temporary directory.
        if (native is { SupportsManagedTemporaryDirectory: false })
            return null;

        // An exact shell retry already received directory advice. A native replacement is a new call and cannot use that retry.
        if (native is null && context.Approval.ManagedTemporaryRetry is not null)
            return null;

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

        // Relocation changes the directory, so project advice for the original directory no longer applies.
        var temporary = policy.EvaluateShellTemporaryCorrection(analysis, candidates, toolCall.Arguments, context.Invocation);
        if (temporary is not null)
            return temporary;

        // Project advice applies to shell calls. The replacement native tool must pass its own policy checks.
        if (native is not null)
            return null;

        if (isMessy)
            return candidates.Count == 0
                ? SelectOneCallDirectoryCorrection(analysis, toolCall, context)
                : null;

        return GetAvailableProjectCorrection(candidates, analysis.WorkingDirectory, context.Invocation);
    }

    private static ToolCorrection.ShellWorkingDirectorySuggested? SelectOneCallDirectoryCorrection(
        ShellCommandAnalysis analysis,
        FunctionCallContent toolCall,
        ToolExecutionContext context)
    {
        if (analysis.Environment.Grammar != ShellGrammar.Bash
            || !analysis.IsResolved
            || analysis.Commands.Count < 2
            || !string.IsNullOrWhiteSpace(ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory"))
            || context.Invocation.ProjectDirectory is not { } projectDirectory)
        {
            return null;
        }

        var first = analysis.Commands[0];
        if (!first.IsComplete
            || first.ImmediateRole != CommandOccurrenceRole.Ordinary
            || first.WorkingDirectoryEffect is not ShellWorkingDirectoryEffect.ChangesOnSuccess
            { Target: ShellValueDomain.Exact exact }
            || first.Ancestry.Count != 2
            || first.Ancestry[0] is not { Ancestor: ShellBlockSyntax, Region: CommandAncestryRegion.Root }
            || first.Ancestry[1] is not
            { Ancestor: CommandListSyntax list, Region: CommandAncestryRegion.Statement, ChildIndex: 0 }
            || list.Items.Count < 2
            || list.Items[0] is not { Operator: CompoundOperator.None, Command: SimpleCommandSyntax simple }
            || !ReferenceEquals(simple.Clause, first.Clause)
            || list.Items[1].Operator != CompoundOperator.AndIf
            || exact.Value.Any(char.IsControl)
            || !ShellPathRules.TryNormalize(exact.Value, ShellPathStyle.Posix, out var target))
        {
            return null;
        }

        try
        {
            if (!PathUtility.IsWithinRoot(target, projectDirectory)
                || PathUtility.AreEquivalentPaths(target, projectDirectory)
                || PathUtility.ContainsSymlinkSegment(projectDirectory, target)
                || !Directory.Exists(target))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }

        return new ToolCorrection.ShellWorkingDirectorySuggested(target);
    }

    private ToolCorrection.ProjectDirectorySuggested? GetAvailableProjectCorrection(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? workingDirectory,
        ToolInvocationContext invocation)
    {
        var project = policy.EvaluateShellProjectCorrection(candidates, workingDirectory, invocation);
        if (project is null)
            return null;

        if (registry.GetByName(SetWorkingDirectoryTool.ToolName) is not SetWorkingDirectoryTool declaration)
            return null;

        if (!policy.IsToolExposed(declaration, invocation))
            return null;

        if (!declaration.CanDeclare(project.Directory, invocation))
            return null;

        return project;
    }

    private async Task<ToolAuthorizationDecision> EvaluatePolicyAsync(
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
            cancellationToken.ThrowIfCancellationRequested();
            if (RequiresExactApproval(projection))
            {
                return CompleteOneTimeOrPrompt(evaluation, toolCall.Name, corrections);
            }

            ValidateCandidateSyntax(projection);
            cancellationToken.ThrowIfCancellationRequested();

            if (HasProtectedIntentPath(projection))
            {
                return evaluation.Complete(
                    ToolAuthorizationDecision.Deny("shell_references_protected_path"));
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (HasIneligibleIntentDirectory(projection))
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
            evaluation.ApplyActorEvidence(actorResult);
            cancellationToken.ThrowIfCancellationRequested();
            if (_approvalEvidence.IsAvailable)
            {
                foreach (var candidate in evaluation.Candidates.Where(static item =>
                             item.Role == ShellPolicyCandidateRole.Ordinary
                             && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
                {
                    evaluation.Cover(
                        candidate,
                        ShellCoverageKind.ApprovalExemptSideEffect);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (projection.InteractiveApproval is InteractiveApprovalCapability.Available)
            {
                ApplyReviewedSafeCoverage(evaluation, policy, context.Invocation);
            }
            cancellationToken.ThrowIfCancellationRequested();

            return CompleteAfterCoverage(
                evaluation,
                context,
                toolCall.Name,
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

    private static bool RequiresExactApproval(ShellPolicyProjection projection)
    {
        // Unresolved syntax needs an exact approval unless the causal projection supplies the missing intent.
        if (projection.ApprovalContext.IsMessy && !projection.HasCausalIntent)
            return true;

        if (projection.Candidates.Count == 0)
            return true;

        foreach (var candidate in projection.Candidates)
        {
            if (candidate.Candidate.Shell is null)
                return true;

            if (candidate.Candidate.VerbTokens is null)
                return true;
        }

        return false;
    }

    private static void ValidateCandidateSyntax(ShellPolicyProjection projection)
    {
        var expectedShell = projection.Environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        foreach (var candidate in projection.Candidates)
        {
            if (candidate.Candidate.Shell != expectedShell)
                throw new InvalidOperationException("Invalid shell policy projection.");

            // RequiresExactApproval handles missing facts before this validation of supplied facts.
            var tokens = candidate.Candidate.VerbTokens!;
            if (tokens.Count == 0)
                throw new InvalidOperationException("Invalid shell policy projection.");

            foreach (var token in tokens)
            {
                if (token.Length == 0 || token.Any(char.IsWhiteSpace))
                    throw new InvalidOperationException("Invalid shell policy projection.");
            }
        }
    }

    private bool HasProtectedIntentPath(ShellPolicyProjection projection)
    {
        foreach (var candidate in projection.Candidates)
        {
            if (candidate.Role != ShellPolicyCandidateRole.CausalIntentConsumer)
                continue;

            if (policy.CausalIntentReferencesProtectedPath(projection.PathFacts[candidate.Id.Value]))
                return true;
        }

        return false;
    }

    private bool HasIneligibleIntentDirectory(ShellPolicyProjection projection)
    {
        foreach (var candidate in projection.Candidates)
        {
            if (candidate.Role != ShellPolicyCandidateRole.CausalIntentConsumer)
                continue;

            if (candidate.IntentDirectory is not { } directory)
                continue;

            if (!policy.AreCausalIntentDirectoriesEligible(directory, candidate.IntentFallbackDirectories))
                return true;
        }

        return false;
    }

    private static void ApplyReviewedSafeCoverage(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        foreach (var candidate in evaluation.Projection.GrantCandidates)
        {
            if (!candidate.CanUseRealReviewedSafePolicy)
                continue;

            if (evaluation.IsCovered(candidate.Id))
                continue;

            if (!policy.IsReviewedSafeCandidate(
                    candidate,
                    evaluation.Projection.PathFacts[candidate.Id.Value],
                    invocation))
            {
                continue;
            }

            evaluation.Cover(
                candidate,
                ShellCoverageKind.ReviewedSafeReal);
        }

        foreach (var candidate in evaluation.Candidates)
        {
            if (candidate.Role != ShellPolicyCandidateRole.CausalIntentConsumer)
                continue;

            if (evaluation.IsCovered(candidate.Id))
                continue;

            if (!HasCoveredIntentPrerequisites(candidate, evaluation))
                continue;

            if (!policy.IsReviewedSafeIntentCandidate(
                    candidate,
                    evaluation.Projection.PathFacts[candidate.Id.Value],
                    invocation))
            {
                continue;
            }

            evaluation.Cover(
                candidate,
                ShellCoverageKind.ReviewedSafeIntent);
        }
    }

    private static bool HasCoveredIntentPrerequisites(ShellPolicyCandidate candidate, ShellPolicyEvaluation evaluation)
    {
        if (candidate.IntentDirectory is null)
            return false;

        // An intent consumer needs explicit prerequisite evidence; an empty list cannot establish coverage.
        if (candidate.IntentPrerequisites.Count == 0)
            return false;

        foreach (var prerequisite in candidate.IntentPrerequisites)
        {
            if (!evaluation.IsCovered(prerequisite))
                return false;
        }

        return true;
    }

    private static ToolAuthorizationDecision CompleteAfterCoverage(
        ShellPolicyEvaluation evaluation,
        ToolExecutionContext context,
        string toolName,
        ToolCorrectionCollection? corrections,
        CancellationToken cancellationToken)
    {
        var projection = evaluation.Projection;
        var approvalMatches = evaluation.ApprovalMatches;
        var remaining = evaluation.UncoveredCandidates;
        if (remaining.Count > 0)
        {
            var approvalContext = evaluation.GetUncoveredApprovalContext(
                ToolAccessPolicy.GetSessionOwnedApprovalDirectories(context));
            var hasExactOneTimeApproval = projection.HasExactOneTimeApproval(
                toolName,
                approvalContext);
            cancellationToken.ThrowIfCancellationRequested();
            if (hasExactOneTimeApproval)
            {
                foreach (var candidate in remaining)
                    evaluation.Cover(candidate, ShellCoverageKind.OneTime);

                cancellationToken.ThrowIfCancellationRequested();
                return evaluation.Complete(
                    ToolAuthorizationDecision.Allow(
                        ToolAllowReason.OneTimeApproval,
                        approvalMatches));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (evaluation.GrantEvidence?.PersistentStoreFailure is not null)
            {
                return evaluation.Complete(
                    ToolAuthorizationDecision.Deny("approval_store_unavailable"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CompleteApprovalOrCorrection(
                evaluation,
                approvalContext,
                approvalMatches,
                corrections);
        }

        cancellationToken.ThrowIfCancellationRequested();
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
