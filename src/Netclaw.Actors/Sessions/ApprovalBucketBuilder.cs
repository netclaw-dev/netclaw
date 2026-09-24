// -----------------------------------------------------------------------
// <copyright file="ApprovalBucketBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Sessions;

internal abstract record ApprovalGrantScope
{
    private ApprovalGrantScope()
    {
    }

    internal sealed record Session : ApprovalGrantScope
    {
        private Session()
        {
        }

        internal static Session Instance { get; } = new();
    }

    internal sealed record Folder : ApprovalGrantScope
    {
        private Folder(string? workingDirectory, string sessionDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
            WorkingDirectory = workingDirectory;
            SessionDirectory = sessionDirectory;
        }

        internal string? WorkingDirectory { get; }

        internal string SessionDirectory { get; }

        internal static Folder Create(string? workingDirectory, string sessionDirectory) =>
            new(workingDirectory, sessionDirectory);
    }

    internal sealed record Repository : ApprovalGrantScope
    {
        private Repository(string? workingDirectory, string commonDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);
            WorkingDirectory = workingDirectory;
            CommonDirectory = commonDirectory;
        }

        internal string? WorkingDirectory { get; }

        internal string CommonDirectory { get; }

        internal static Repository Create(string? workingDirectory, string commonDirectory) =>
            new(workingDirectory, commonDirectory);
    }

    internal sealed record Global : ApprovalGrantScope
    {
        private Global()
        {
        }

        internal static Global Instance { get; } = new();
    }

    internal static ApprovalGrantScope FromDecision(
        ApprovalDecision decision,
        string? workingDirectory,
        string sessionDirectory,
        string? repositoryCommonDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        return decision switch
        {
            ApprovalDecision.ApprovedSession => Session.Instance,
            ApprovalDecision.ApprovedAlways => Folder.Create(workingDirectory, sessionDirectory),
            ApprovalDecision.ApprovedRepository when !string.IsNullOrWhiteSpace(repositoryCommonDirectory) =>
                Repository.Create(workingDirectory, repositoryCommonDirectory),
            ApprovalDecision.ApprovedRepository =>
                throw new InvalidOperationException("The repository option lacks its offered identity."),
            ApprovalDecision.ApprovedEverywhere => Global.Instance,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "The approval decision cannot create a reusable grant."),
        };
    }
}

internal static class ApprovalBucketBuilder
{
    public static IReadOnlyList<ToolApprovalGrant> BuildGrants(
        IReadOnlyList<ApprovalCandidate> candidates,
        ApprovalGrantScope scope)
    {
        var grantCandidates = candidates
            .Where(static candidate => !ApprovalPatternMatching.IsPureSideEffect(candidate))
            .ToArray();
        return scope switch
        {
            ApprovalGrantScope.Repository repository =>
                BuildRepositoryGrants(grantCandidates, repository),
            _ => BuildDirectoryGrants(grantCandidates, scope),
        };
    }

    private static IReadOnlyList<ToolApprovalGrant> BuildRepositoryGrants(
        IReadOnlyList<ApprovalCandidate> candidates,
        ApprovalGrantScope.Repository repository)
    {
        if (!GitRepositoryApprovalScope.TryResolveCandidates(
                candidates, repository.WorkingDirectory, out var repositoryScopes)
            || !ToolApprovalEntryComparer.Equals(
                repositoryScopes![0].CommonDirectory, repository.CommonDirectory))
        {
            throw new InvalidOperationException("The repository identity changed after the prompt.");
        }

        var grants = new List<ToolApprovalGrant>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var repositoryScope = repositoryScopes[index];
            var resolvedCandidate = candidate with
            {
                Directory = repositoryScope.ResolvedDirectory,
            };
            grants.Add(new ToolApprovalGrant(resolvedCandidate, Directory: null)
            {
                Repository = repositoryScope.CommonDirectory,
                RepositoryWorktree = repositoryScope.WorktreeRoot,
            });
        }

        return grants;
    }

    private static IReadOnlyList<ToolApprovalGrant> BuildDirectoryGrants(
        IReadOnlyList<ApprovalCandidate> candidates,
        ApprovalGrantScope scope)
    {
        var grants = new List<ToolApprovalGrant>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var effectiveDirectory = ResolveDirectory(candidate, scope);
            if (scope is ApprovalGrantScope.Folder folder
                && effectiveDirectory is not null
                && PathUtility.AreEquivalentPaths(effectiveDirectory, folder.SessionDirectory))
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
    /// a working-directory fallback. The session approval store matches without
    /// folder scope, so threading cwd through here creates buckets that the
    /// session-owned guard can drop for standalone verbs such as curl or git status.
    ///
    /// Persistent scope still falls back to the working directory and applies
    /// the session-owned guard so folder-scoped grants pointing at the session
    /// directory are not saved as dead-on-arrival approvals.
    /// </remarks>
    public static Dictionary<string, List<string>> Build(
        IReadOnlyList<ApprovalCandidate> candidates,
        ApprovalGrantScope scope)
    {
        if (scope is ApprovalGrantScope.Repository)
            throw new InvalidOperationException("Repository grants require structured approval storage.");

        var grouping = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var grant in BuildGrants(candidates, scope))
        {
            var key = grant.Directory ?? string.Empty;
            if (!grouping.TryGetValue(key, out var verbs))
            {
                verbs = [];
                grouping[key] = verbs;
            }

            if (!verbs.Contains(grant.Candidate.Verb, StringComparer.OrdinalIgnoreCase))
                verbs.Add(grant.Candidate.Verb);
        }

        return grouping;
    }

    private static string? ResolveDirectory(
        ApprovalCandidate candidate,
        ApprovalGrantScope scope)
        => scope switch
        {
            ApprovalGrantScope.Session => candidate.Directory,
            ApprovalGrantScope.Folder folder => candidate.Directory ?? folder.WorkingDirectory,
            ApprovalGrantScope.Global => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "The grant decision is invalid."),
        };
}
