// -----------------------------------------------------------------------
// <copyright file="ApprovalBucketBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Sessions;

internal sealed record ApprovalGrantContext
{
    private ApprovalGrantContext(
        ApprovalDecision decision,
        string? workingDirectory,
        string sessionDirectory,
        string? repositoryCommonDirectory)
    {
        Decision = decision;
        WorkingDirectory = workingDirectory;
        SessionDirectory = sessionDirectory;
        RepositoryCommonDirectory = repositoryCommonDirectory;
    }

    public ApprovalDecision Decision { get; }

    public string? WorkingDirectory { get; }

    public string SessionDirectory { get; }

    public string? RepositoryCommonDirectory { get; }

    public bool IsPersistent => Decision is not ApprovalDecision.ApprovedSession;

    public static ApprovalGrantContext FromDecision(
        ApprovalDecision decision,
        string? workingDirectory,
        string sessionDirectory,
        string? repositoryCommonDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (decision is not (
                ApprovalDecision.ApprovedSession
                or ApprovalDecision.ApprovedAlways
                or ApprovalDecision.ApprovedRepository
                or ApprovalDecision.ApprovedEverywhere))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "The approval decision cannot create a reusable grant.");
        }

        if (decision == ApprovalDecision.ApprovedRepository
            && string.IsNullOrWhiteSpace(repositoryCommonDirectory))
        {
            throw new InvalidOperationException("The repository option lacks its offered identity.");
        }

        return new ApprovalGrantContext(
            decision, workingDirectory, sessionDirectory, repositoryCommonDirectory);
    }
}

internal static class ApprovalBucketBuilder
{
    public static IReadOnlyList<ToolApprovalGrant> BuildGrants(
        IReadOnlyList<ApprovalCandidate> candidates,
        ApprovalGrantContext context)
    {
        var grants = new List<ToolApprovalGrant>(candidates.Count);
        GitRepositoryApprovalScope? repositoryScope = null;
        if (context.Decision == ApprovalDecision.ApprovedRepository
            && (!GitRepositoryApprovalScope.TryResolve(context.WorkingDirectory, out repositoryScope)
                || !ToolApprovalEntryComparer.Equals(
                    repositoryScope!.CommonDirectory, context.RepositoryCommonDirectory!)))
        {
            throw new InvalidOperationException("The repository identity changed after the prompt.");
        }

        if (repositoryScope is not null
            && candidates.Any(candidate => !repositoryScope.Contains(candidate.Directory, context.WorkingDirectory)))
        {
            throw new InvalidOperationException("An approval candidate is outside the registered worktree.");
        }

        foreach (var candidate in candidates)
        {
            if (ApprovalPatternMatching.IsPureSideEffect(candidate))
            {
                continue;
            }

            if (repositoryScope is not null)
            {
                grants.Add(new ToolApprovalGrant(candidate, Directory: null)
                {
                    Repository = repositoryScope.CommonDirectory,
                    RepositoryWorktree = context.WorkingDirectory,
                });
                continue;
            }

            var effectiveDirectory = ResolveDirectory(
                candidate,
                context);
            if (context.IsPersistent && effectiveDirectory is not null
                && PathUtility.AreEquivalentPaths(effectiveDirectory, context.SessionDirectory))
            {
                continue;
            }

            grants.Add(new ToolApprovalGrant(candidate, effectiveDirectory));
        }

        return grants;
    }

    /// <summary>
    /// Groups approval candidates into the per-directory buckets that become
    /// <c>RecordApprovalAsync</c> calls.
    /// </summary>
    /// <remarks>
    /// Session-scope entries use <c>candidate.Directory</c> directly without
    /// a working-directory fallback. The session approval dictionary
    /// matches verb-only, so threading cwd through here creates buckets that the
    /// session-owned guard can drop for standalone verbs such as curl or git status.
    ///
    /// Persistent scope still falls back to the working directory and applies
    /// the session-owned guard so folder-scoped grants pointing at the session
    /// directory are not saved as dead-on-arrival approvals.
    /// </remarks>
    public static Dictionary<string, List<string>> Build(
        IReadOnlyList<ApprovalCandidate> candidates,
        ApprovalGrantContext context)
    {
        if (context.Decision == ApprovalDecision.ApprovedRepository)
            throw new InvalidOperationException("Repository grants require structured approval storage.");

        var grouping = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (ApprovalPatternMatching.IsPureSideEffect(candidate))
                continue;

            var effectiveDirectory = ResolveDirectory(
                candidate,
                context);
            if (context.IsPersistent && effectiveDirectory is not null
                && PathUtility.AreEquivalentPaths(effectiveDirectory, context.SessionDirectory))
            {
                continue;
            }

            var key = effectiveDirectory ?? string.Empty;
            if (!grouping.TryGetValue(key, out var verbs))
            {
                verbs = [];
                grouping[key] = verbs;
            }

            if (!verbs.Contains(candidate.Verb, StringComparer.OrdinalIgnoreCase))
                verbs.Add(candidate.Verb);
        }

        return grouping;
    }

    private static string? ResolveDirectory(
        ApprovalCandidate candidate,
        ApprovalGrantContext context)
        => context.Decision switch
        {
            ApprovalDecision.ApprovedSession => candidate.Directory,
            ApprovalDecision.ApprovedAlways => candidate.Directory ?? context.WorkingDirectory,
            ApprovalDecision.ApprovedEverywhere => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(context),
                context.Decision,
                "The grant decision is invalid."),
        };
}
