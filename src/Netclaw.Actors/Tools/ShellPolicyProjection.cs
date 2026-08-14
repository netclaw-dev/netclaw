// -----------------------------------------------------------------------
// <copyright file="ShellPolicyProjection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal enum ShellCoverageKind
{
    Uncovered = 0,
    OneTime = 1,
    Session = 2,
    PersistentGlobal = 3,
    PersistentFolder = 4,
    ReviewedSafePolicy = 5,
    Denied = 6,
}

internal enum ShellPolicyReason
{
    None = 0,
    OneTimeGrant = 1,
    SessionGrant = 2,
    PersistentGlobalGrant = 3,
    PersistentFolderGrant = 4,
    ReviewedSafePhrase = 5,
    ApprovalExemptSideEffect = 6,
}

internal readonly record struct ShellPolicyCandidateId
{
    internal ShellPolicyCandidateId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    internal int Value { get; }
}

internal enum ShellPolicyCandidateRole
{
    Ordinary = 0,
    CausalPrerequisite = 1,
    CausalIntentConsumer = 2,
}

internal sealed record ShellPolicyCandidate(
    ShellPolicyCandidateId Id,
    ApprovalCandidate Candidate,
    ShellSyntaxTree.CommandOccurrence? SourceOccurrence)
{
    internal ShellPolicyCandidateRole Role { get; init; }

    internal string? IntentDirectory { get; init; }

    internal IReadOnlyList<string> IntentFallbackDirectories { get; init; } = [];

    internal IReadOnlyList<ShellPolicyCandidateId> IntentPrerequisites { get; init; } = [];

    internal bool CanMatchStoredGrant => Role != ShellPolicyCandidateRole.CausalIntentConsumer;

    internal bool CanUseRealReviewedSafePolicy => Role == ShellPolicyCandidateRole.Ordinary;
}

internal sealed record ShellCandidateCoverage(
    ShellPolicyCandidateId CandidateId,
    ShellCoverageKind Kind,
    ShellPolicyReason Reason);

/// <summary>
/// The immutable policy-facing projection of one shell approval context.
/// </summary>
internal sealed record ShellPolicyProjection
{
    private ShellPolicyProjection(
        ShellExecutionEnvironment environment,
        ShellCommandAnalysis? execution,
        ToolRunScope runScope,
        ToolApprovalContext approvalContext,
        IReadOnlyList<ShellPolicyCandidate> candidates,
        ShellPolicyPathFacts pathFacts,
        IReadOnlySet<string> approvedOneTimeKeys,
        string? approvedOneTimeToolName)
    {
        Environment = environment;
        Execution = execution;
        RunScope = runScope;
        ApprovalContext = approvalContext;
        Candidates = candidates;
        PathFacts = pathFacts;
        ApprovedOneTimeKeys = approvedOneTimeKeys;
        ApprovedOneTimeToolName = approvedOneTimeToolName;
    }

    internal ShellExecutionEnvironment Environment { get; }

    internal ShellCommandAnalysis? Execution { get; }

    internal ToolRunScope RunScope { get; }

    internal ToolApprovalContext ApprovalContext { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates { get; }

    internal ShellPolicyPathFacts PathFacts { get; }

    internal IReadOnlySet<string> ApprovedOneTimeKeys { get; }

    internal string? ApprovedOneTimeToolName { get; }

    internal IReadOnlyList<ShellPolicyCandidate> GrantCandidates =>
        Candidates
            .Where(static candidate =>
                candidate.CanMatchStoredGrant
                &&
                !ApprovalPatternMatching.IsPureSideEffect(candidate.Candidate))
            .ToArray();

    internal bool HasCausalIntent => Candidates.Any(static candidate =>
        candidate.Role != ShellPolicyCandidateRole.Ordinary);

    internal bool HasExactOneTimeApproval(
        string toolName,
        ToolApprovalContext approvalContext)
        => OneTimeApprovalKeys.Matches(
            ApprovedOneTimeToolName,
            ApprovedOneTimeKeys,
            toolName,
            approvalContext);

    internal static bool TryCreate(
        ShellExecutionEnvironment environment,
        ShellApprovalMatcher matcher,
        ShellCommandAnalysis? execution,
        ToolApprovalContext approvalContext,
        ToolExecutionContext context,
        Func<string, bool> isAllowedHostPath,
        out ShellPolicyProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(approvalContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(isAllowedHostPath);

        projection = null;
        if (approvalContext.Candidates is null)
            return false;

        if (approvalContext.IsMessy
            && approvalContext.Candidates.Count == 0
            && execution is not null
            && BashCausalApprovalIntent.TryProject(
                environment,
                execution,
                matcher,
                isAllowedHostPath,
                out var causalCandidates))
        {
            return TryCreateCausal(
                environment,
                execution,
                approvalContext,
                context,
                causalCandidates,
                out projection);
        }

        var candidates = new ShellPolicyCandidate[approvalContext.Candidates.Count];
        var candidateCopies = new ApprovalCandidate[approvalContext.Candidates.Count];
        for (var index = 0; index < approvalContext.Candidates.Count; index++)
        {
            var source = approvalContext.Candidates[index];
            if (source is null)
                return false;

            var copy = source with
            {
                VerbTokens = source.VerbTokens is null
                    ? null
                    : Array.AsReadOnly(source.VerbTokens.ToArray()),
                SourceOccurrence = null
            };
            candidateCopies[index] = copy;
            candidates[index] = new ShellPolicyCandidate(
                new ShellPolicyCandidateId(index),
                copy,
                source.SourceOccurrence);
        }

        var contextCopy = approvalContext with
        {
            Patterns = Array.AsReadOnly(approvalContext.Patterns.ToArray()),
            CandidateVerbs = Array.AsReadOnly(approvalContext.CandidateVerbs.ToArray()),
            Options = Array.AsReadOnly(approvalContext.Options.ToArray()),
            Candidates = Array.AsReadOnly(candidateCopies)
        };
        var runScopeCopy = context.RunScope with
        {
            RecentFiles = Array.AsReadOnly(context.RunScope.RecentFiles.ToArray())
        };
        var candidateView = Array.AsReadOnly(candidates);
        projection = new ShellPolicyProjection(
            environment,
            execution,
            runScopeCopy,
            contextCopy,
            candidateView,
            ShellPolicyPathFacts.Create(candidateView, environment.PathStyle),
            context.Approval.OneTimeApprovedPatterns.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            context.Approval.OneTimeApprovedToolName);
        return true;
    }

    private static bool TryCreateCausal(
        ShellExecutionEnvironment environment,
        ShellCommandAnalysis execution,
        ToolApprovalContext approvalContext,
        ToolExecutionContext context,
        IReadOnlyList<BashCausalApprovalCandidate> causalCandidates,
        out ShellPolicyProjection? projection)
    {
        projection = null;
        var candidates = new ShellPolicyCandidate[causalCandidates.Count];
        for (var index = 0; index < causalCandidates.Count; index++)
        {
            var source = causalCandidates[index];
            if (source.PrerequisiteIndexes.Any(prerequisite =>
                    prerequisite < 0 || prerequisite >= causalCandidates.Count))
            {
                return false;
            }

            var candidateCopy = source.Candidate with
            {
                VerbTokens = source.Candidate.VerbTokens is null
                    ? null
                    : Array.AsReadOnly(source.Candidate.VerbTokens.ToArray()),
                SourceOccurrence = null
            };
            candidates[index] = new ShellPolicyCandidate(
                new ShellPolicyCandidateId(index),
                candidateCopy,
                source.SourceOccurrence)
            {
                Role = source.Role,
                IntentDirectory = source.IntentDirectory,
                IntentFallbackDirectories = Array.AsReadOnly(source.FallbackDirectories.ToArray()),
                IntentPrerequisites = Array.AsReadOnly(source.PrerequisiteIndexes
                    .Select(static prerequisite => new ShellPolicyCandidateId(prerequisite))
                    .ToArray())
            };
        }

        var contextCopy = approvalContext with
        {
            Patterns = Array.AsReadOnly(approvalContext.Patterns.ToArray()),
            CandidateVerbs = Array.AsReadOnly(approvalContext.CandidateVerbs.ToArray()),
            Options = Array.AsReadOnly(approvalContext.Options.ToArray()),
            Candidates = Array.AsReadOnly(approvalContext.Candidates!.ToArray())
        };
        var runScopeCopy = context.RunScope with
        {
            RecentFiles = Array.AsReadOnly(context.RunScope.RecentFiles.ToArray())
        };
        var candidateView = Array.AsReadOnly(candidates);
        projection = new ShellPolicyProjection(
            environment,
            execution,
            runScopeCopy,
            contextCopy,
            candidateView,
            ShellPolicyPathFacts.Create(candidateView, environment.PathStyle),
            context.Approval.OneTimeApprovedPatterns.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            context.Approval.OneTimeApprovedToolName);
        return true;
    }
}
