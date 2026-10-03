// -----------------------------------------------------------------------
// <copyright file="ShellPolicyCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Supplies the shell facts that <see cref="Netclaw.Actors.Authorization.ToolAuthorizer"/>
/// asks for after the screens: the applicable advice, the coverage of each
/// candidate (one batched stored-grant lookup, the side-effect exemption, and
/// the reviewed-safe policy), and the completed decision. The authorizer owns
/// the order.
/// </summary>
internal sealed class ShellPolicyCoordinator(
    ToolRegistry registry,
    ToolAccessPolicy policy,
    IToolApprovalService? approvalService)
{
    /// <summary>Collects compatible advice from the same invocation and its existing policies.</summary>
    internal ToolCorrectionCollection? CollectApplicableCorrections(
        ShellCommandAnalysis analysis,
        FunctionCallContent toolCall,
        ToolExecutionContext context,
        ShellPolicyPreflightResult preflight)
    {
        // Denial and approval without command analysis cannot become advice to submit a different call.
        if (preflight is ShellPolicyPreflightResult.Complete
            { Decision.Outcome: not ToolAuthorizationOutcome.Allowed })
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
        if (preflight is ShellPolicyPreflightResult.Continue { DirectoryScopes.IsCausalList: true })
        {
            // A causal list received one-call advice before its candidates came from the
            // directory proof. It keeps that advice, and it gets no project advice.
            candidates = [];
            isMessy = true;
        }
        else if (preflight is ShellPolicyPreflightResult.Continue continuation)
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
        if (first.WorkingDirectoryEffect is not ShellWorkingDirectoryEffect.ChangesOnSuccess
            { Target: ShellValueDomain.Exact exact }
            || !BashDirectoryScopeProjection.TryGetListItem(first, 0, out var list)
            || list.Items.Count < 2
            || list.Items[0].Operator != CompoundOperator.None
            || list.Items[1].Operator != CompoundOperator.AndIf
            || exact.Value.Any(char.IsControl)
            || !CanonicalPath.TryCreate(exact.Value, relativeBase: null, ShellPathStyle.Posix, out var target)
            || !CanonicalPath.TryCreateHost(projectDirectory, relativeBase: null, out var project))
        {
            return null;
        }

        // Advice only: the target must be strictly below the project without a link.
        if (target.IsSamePath(project)
            || FileSystemAuthority.EvaluateMembership(
                target,
                [new PathBoundary.Folder(project, LinkRule.BelowRoot)]) is not PathDecision.Allowed
            || !Directory.Exists(target.Value))
        {
            return null;
        }

        return new ToolCorrection.ShellWorkingDirectorySuggested(target.Value);
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

    /// <summary>
    /// Covers the candidates of a resolved shell call: one batched stored-grant
    /// lookup, then the side-effect exemption, then (interactive only) the
    /// reviewed-safe policy.
    /// </summary>
    internal async Task CoverAsync(
        INetclawTool tool,
        ToolExecutionContext context,
        ShellPolicyEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var projection = evaluation.Projection;
        ValidateCandidateSyntax(projection);
        cancellationToken.ThrowIfCancellationRequested();

        var grantCandidates = evaluation.GrantCandidates;
        var requestCandidates = grantCandidates
            .Select(state => new ShellGrantCandidate(
                state.Candidate.Id,
                state.Candidate.Candidate,
                projection.ApprovalContext.Cwd))
            .ToArray();
        var actorResult = await MatchStoredGrantsAsync(
            new ShellApprovalMatchRequest(
                ToApprovalSessionId(context.SessionId),
                context.Audience,
                new ToolName(tool.Name),
                Array.AsReadOnly(requestCandidates)),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        evaluation.ApplyActorEvidence(actorResult);
        cancellationToken.ThrowIfCancellationRequested();
        if (approvalService is not null)
        {
            foreach (var candidate in evaluation.Candidates.Where(static item =>
                         item.Role == ShellPolicyCandidateRole.Ordinary
                         && ApprovalPatternMatching.IsPureSideEffect(item.Candidate)))
            {
                evaluation.Cover(candidate, Coverage.Exempt.Instance);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (projection.InteractiveApproval is InteractiveApprovalCapability.Available)
        {
            ApplyReviewedSafeCoverage(evaluation, policy, context.Invocation);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static bool RequiresExactApproval(ShellPolicyProjection projection)
    {
        // Unresolved syntax needs an exact approval.
        if (projection.ApprovalContext.IsMessy)
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
        if (!HasValidCandidateSyntax(projection))
            throw new InvalidOperationException("Invalid shell policy projection.");
    }

    /// <summary>
    /// True when every candidate has the shell of the projection and nonempty
    /// verb tokens without whitespace. A grant lookup needs these facts.
    /// </summary>
    internal static bool HasValidCandidateSyntax(ShellPolicyProjection projection)
    {
        var expectedShell = projection.Environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        foreach (var candidate in projection.Candidates)
        {
            if (candidate.Candidate.Shell != expectedShell)
                return false;

            // RequiresExactApproval handles missing facts before this validation of supplied facts.
            var tokens = candidate.Candidate.VerbTokens!;
            if (tokens.Count == 0 || tokens.Any(static token => token.Length == 0 || token.Any(char.IsWhiteSpace)))
                return false;
        }

        return true;
    }

    private static void ApplyReviewedSafeCoverage(
        ShellPolicyEvaluation evaluation,
        ToolAccessPolicy policy,
        ToolInvocationContext invocation)
    {
        foreach (var state in evaluation.GrantCandidates)
        {
            var candidate = state.Candidate;
            if (!candidate.CanUseRealReviewedSafePolicy)
                continue;

            if (evaluation.IsCovered(candidate.Id))
                continue;

            if (!policy.IsReviewedSafeCandidate(
                    candidate,
                    state.PathFacts,
                    invocation))
            {
                continue;
            }

            evaluation.Cover(
                candidate,
                new Coverage.ReviewedSafe(ReviewedSafeRoot.Real));
        }

        foreach (var state in evaluation.CandidateStates)
        {
            var candidate = state.Candidate;
            if (candidate.Role != ShellPolicyCandidateRole.CausalIntentConsumer)
                continue;

            if (evaluation.IsCovered(candidate.Id))
                continue;

            // The directory change and its action stay candidates, so an allowed
            // result still needs their own coverage.
            if (!policy.IsReviewedSafeIntentCandidate(
                    candidate,
                    state.PathFacts,
                    invocation))
            {
                continue;
            }

            evaluation.Cover(
                candidate,
                new Coverage.ReviewedSafe(ReviewedSafeRoot.Intent));
        }
    }

    /// <summary>
    /// Completes a shell call with uncovered candidates: a one-time answer, a
    /// store failure, advice, or a consent request for the uncovered candidates only.
    /// </summary>
    internal static ToolAuthorizationDecision CompleteUncovered(
        ShellPolicyEvaluation evaluation,
        ToolExecutionContext context,
        string toolName,
        ToolCorrectionCollection? corrections,
        CancellationToken cancellationToken)
    {
        var projection = evaluation.Projection;
        var approvalMatches = evaluation.ApprovalMatches;
        var remaining = evaluation.UncoveredCandidates;
        var approvalContext = evaluation.GetUncoveredApprovalContext(
            ToolAccessPolicy.GetSessionOwnedApprovalDirectories(context));
        var hasExactOneTimeApproval = projection.HasExactOneTimeApproval(
            toolName,
            approvalContext);
        cancellationToken.ThrowIfCancellationRequested();
        if (hasExactOneTimeApproval)
        {
            foreach (var candidate in remaining)
                evaluation.Cover(candidate, Coverage.OneTime.Instance);

            cancellationToken.ThrowIfCancellationRequested();
            return evaluation.Complete(
                ToolAuthorizationDecision.Allow(
                    ToolAllowReason.OneTimeApproval,
                    approvalMatches));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (evaluation.PersistentStoreFailure is not null)
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

    /// <summary>Completes a shell call whose every candidate has coverage.</summary>
    /// <param name="evaluation">The covered candidates.</param>
    /// <param name="grantReplacedTrustedRoot">
    /// True when stored grants replaced a trusted-root denial (PR 6e). It changes
    /// only the allow reason, not the outcome or the matched grants.
    /// </param>
    /// <param name="cancellationToken">The call cancellation.</param>
    internal static ToolAuthorizationDecision CompleteCovered(
        ShellPolicyEvaluation evaluation,
        bool grantReplacedTrustedRoot,
        CancellationToken cancellationToken)
    {
        var approvalMatches = evaluation.ApprovalMatches;
        cancellationToken.ThrowIfCancellationRequested();
        var grantCandidateCount = evaluation.GrantCandidates.Count();
        if (approvalMatches.Count > 0)
        {
            return evaluation.Complete(
                ToolAuthorizationDecision.Allow(
                    grantReplacedTrustedRoot
                        ? ToolAllowReason.StoredApprovalOutsideTrustedRoots
                        : ToolAllowReason.StoredApproval,
                    approvalMatches));
        }

        return evaluation.Complete(
            ToolAuthorizationDecision.Allow(
                grantCandidateCount == 0
                    ? ToolAllowReason.ApprovalExemptShellCandidates
                    : ToolAllowReason.ReviewedSafePolicy));
    }

    internal static ToolAuthorizationDecision CompleteOneTimeOrPrompt(
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

    internal static ToolAuthorizationDecision Complete(
        ToolAuthorizationDecision decision,
        IReadOnlyList<ToolApprovalMatch> approvalMatches,
        ShellPolicyDecisionTraceBuilder trace)
        => CompleteWithTrace(decision.WithApprovalMatches(approvalMatches), trace);

    private static ToolAuthorizationDecision CompleteWithTrace(
        ToolAuthorizationDecision decision,
        ShellPolicyDecisionTraceBuilder trace)
        => decision.WithShellPolicyTrace(trace.Complete(decision));

    /// <summary>
    /// Asks the approval actor which stored grants cover the candidates. With no
    /// approval service, no stored grant exists, so every candidate stays uncovered.
    /// </summary>
    internal async Task<ShellApprovalMatchResult> MatchStoredGrantsAsync(
        ShellApprovalMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0 || approvalService is null)
        {
            return ShellApprovalMatchResult.Create(
                request.Candidates,
                persistentStoreFailure: null,
                request.Candidates
                    .Select(static candidate => ShellGrantCandidateResult.Uncovered(candidate))
                    .ToArray());
        }

        // Shell grants need per-candidate evidence. An approval service without
        // it cannot prove which grant covered which candidate, so fail loudly.
        if (approvalService is not IShellApprovalMatchService shellApprovalService)
        {
            throw new InvalidOperationException(
                "The approval service cannot match shell candidates.");
        }

        var result = await shellApprovalService.MatchShellCandidatesAsync(request, cancellationToken);
        ArgumentNullException.ThrowIfNull(result);
        return ShellApprovalMatchResult.Create(
            request.Candidates,
            result.PersistentStoreFailure,
            result.Candidates);
    }

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}
