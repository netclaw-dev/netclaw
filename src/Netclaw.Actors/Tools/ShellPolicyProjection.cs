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

internal sealed record ShellPolicyCandidate(
    ShellPolicyCandidateId Id,
    ApprovalCandidate Candidate);

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
        IReadOnlySet<string> approvedOneTimeKeys,
        string? approvedOneTimeToolName)
    {
        Environment = environment;
        Execution = execution;
        RunScope = runScope;
        ApprovalContext = approvalContext;
        Candidates = candidates;
        ApprovedOneTimeKeys = approvedOneTimeKeys;
        ApprovedOneTimeToolName = approvedOneTimeToolName;
    }

    internal ShellExecutionEnvironment Environment { get; }

    internal ShellCommandAnalysis? Execution { get; }

    internal ToolRunScope RunScope { get; }

    internal ToolApprovalContext ApprovalContext { get; }

    internal IReadOnlyList<ShellPolicyCandidate> Candidates { get; }

    internal IReadOnlySet<string> ApprovedOneTimeKeys { get; }

    internal string? ApprovedOneTimeToolName { get; }

    internal IReadOnlyList<ShellPolicyCandidate> GrantCandidates =>
        Candidates
            .Where(static candidate =>
                !ApprovalPatternMatching.IsPureSideEffect(candidate.Candidate))
            .ToArray();

    internal bool HasExactOneTimeApproval(
        string toolName,
        ToolApprovalContext approvalContext)
    {
        if (string.IsNullOrEmpty(ApprovedOneTimeToolName)
            || !string.Equals(ApprovedOneTimeToolName, toolName, StringComparison.Ordinal))
        {
            return false;
        }

        return ApprovedOneTimeKeys.SetEquals(OneTimeApprovalKeys.Create(approvalContext));
    }

    internal static bool TryCreate(
        ShellExecutionEnvironment environment,
        ShellCommandAnalysis? execution,
        ToolApprovalContext approvalContext,
        ToolExecutionContext context,
        out ShellPolicyProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(approvalContext);
        ArgumentNullException.ThrowIfNull(context);

        projection = null;
        if (approvalContext.Candidates is null)
            return false;

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
                    : Array.AsReadOnly(source.VerbTokens.ToArray())
            };
            candidateCopies[index] = copy;
            candidates[index] = new ShellPolicyCandidate(
                new ShellPolicyCandidateId(index),
                copy);
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
        projection = new ShellPolicyProjection(
            environment,
            execution,
            runScopeCopy,
            contextCopy,
            Array.AsReadOnly(candidates),
            context.Approval.OneTimeApprovedPatterns.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            context.Approval.OneTimeApprovedToolName);
        return true;
    }
}

internal sealed class ShellCoverageSet
{
    private readonly Dictionary<ShellPolicyCandidateId, ShellCandidateCoverage> _coverage;

    internal ShellCoverageSet(IReadOnlyList<ShellPolicyCandidate> candidates)
    {
        _coverage = new Dictionary<ShellPolicyCandidateId, ShellCandidateCoverage>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!_coverage.TryAdd(
                    candidate.Id,
                    new ShellCandidateCoverage(
                        candidate.Id,
                        ShellCoverageKind.Uncovered,
                        ShellPolicyReason.None)))
            {
                throw new InvalidOperationException("Duplicate shell candidate id.");
            }
        }
    }

    internal IReadOnlyList<ShellPolicyCandidateId> UncoveredIds => _coverage.Values
        .Where(static item => item.Kind == ShellCoverageKind.Uncovered)
        .Select(static item => item.CandidateId)
        .ToArray();

    internal bool AllCovered => _coverage.Values.All(static item =>
        item.Kind is not ShellCoverageKind.Uncovered and not ShellCoverageKind.Denied);

    internal void Cover(
        ShellPolicyCandidateId candidateId,
        ShellCoverageKind kind,
        ShellPolicyReason reason)
    {
        if (kind is ShellCoverageKind.Uncovered or ShellCoverageKind.Denied)
            throw new InvalidOperationException("Invalid shell coverage transition.");

        if (!_coverage.TryGetValue(candidateId, out var current)
            || current.Kind != ShellCoverageKind.Uncovered)
        {
            throw new InvalidOperationException("Shell candidate coverage can be assigned once.");
        }

        _coverage[candidateId] = new ShellCandidateCoverage(candidateId, kind, reason);
    }
}
