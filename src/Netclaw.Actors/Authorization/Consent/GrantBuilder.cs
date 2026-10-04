// -----------------------------------------------------------------------
// <copyright file="GrantBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Security.Authorization.Filesystem;

namespace Netclaw.Actors.Authorization.Consent;

/// <summary>
/// Resolves an operator's grant answer to one grant for each reviewed candidate.
/// </summary>
/// <remarks>
/// Owner: the Consent context. Data: call-local. The answer names only a
/// <see cref="GrantScopeKind"/>; this builder adds the facts of the prompt.
/// </remarks>
internal static class GrantBuilder
{
    /// <summary>
    /// Builds the grants for <paramref name="candidates"/>.
    /// </summary>
    /// <param name="candidates">The candidates that the operator reviewed.</param>
    /// <param name="kind">The scope kind of the answer.</param>
    /// <param name="workingDirectory">The prompt's working directory.</param>
    /// <param name="sessionDirectory">The session directory. A folder grant for it is dead on arrival.</param>
    /// <param name="repositoryCommonDirectory">The repository that the prompt offered, or null.</param>
    public static IReadOnlyList<ToolApprovalGrant> Build(
        IReadOnlyList<ApprovalCandidate> candidates,
        GrantScopeKind kind,
        string? workingDirectory,
        string sessionDirectory,
        string? repositoryCommonDirectory)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);

        // Approval-exempt side effects (echo, true, ...) are allowed for the
        // current call, but no grant is stored for them.
        var grantCandidates = candidates
            .Where(static candidate => !ApprovalPatternMatching.IsPureSideEffect(candidate))
            .ToArray();
        return kind switch
        {
            // A session grant matches without a directory, so a verb with no
            // path operand (curl, gh, git status) must not inherit the cwd and
            // then meet the session-directory guard below.
            GrantScopeKind.Session => grantCandidates
                .Select(static candidate => new ToolApprovalGrant(candidate, GrantScope.Session.Instance))
                .ToArray(),
            GrantScopeKind.Folder => BuildFolderGrants(grantCandidates, workingDirectory, sessionDirectory),
            GrantScopeKind.Repository when !string.IsNullOrWhiteSpace(repositoryCommonDirectory) =>
                BuildRepositoryGrants(grantCandidates, workingDirectory, repositoryCommonDirectory),
            GrantScopeKind.Repository =>
                throw new InvalidOperationException("The repository option lacks its offered identity."),
            GrantScopeKind.Everywhere => grantCandidates
                .Select(static candidate => new ToolApprovalGrant(candidate, GrantScope.Everywhere.Instance))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown grant scope kind."),
        };
    }

    private static IReadOnlyList<ToolApprovalGrant> BuildFolderGrants(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? workingDirectory,
        string sessionDirectory)
    {
        var grants = new List<ToolApprovalGrant>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var directory = candidate.Directory ?? workingDirectory;

            // The next session has a new session directory, so a folder grant
            // for this one could never match again.
            if (directory is not null && PathUtility.AreEquivalentPaths(directory, sessionDirectory))
                continue;

            // A prompt with no directory at all stores the v3 "anywhere" shape,
            // as the store did before this type existed. The folder option is
            // offered only when the prompt has a working directory.
            grants.Add(new ToolApprovalGrant(
                candidate,
                directory is null ? GrantScope.Everywhere.Instance : new GrantScope.Folder(directory)));
        }

        return grants;
    }

    private static IReadOnlyList<ToolApprovalGrant> BuildRepositoryGrants(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? workingDirectory,
        string commonDirectory)
    {
        if (!RepositoryIdentity.TryResolveAll(
                candidates.Select(static candidate => candidate.Directory).ToArray(),
                workingDirectory,
                out var repositories)
            || !ToolApprovalEntryComparer.Equals(repositories![0].CommonDirectory, commonDirectory))
        {
            throw new InvalidOperationException("The repository identity changed after the prompt.");
        }

        var grants = new List<ToolApprovalGrant>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var repository = repositories[index];
            grants.Add(new ToolApprovalGrant(
                WithWorktreeProgram(candidates[index], repository.CommonDirectory)
                    with { Directory = repository.ResolvedDirectory },
                new GrantScope.Repository(repository.CommonDirectory))
            {
                RepositoryWorktree = repository.WorktreeRoot,
            });
        }

        return grants;
    }

    /// <summary>
    /// Stores a program file of the repository by its path below the worktree root
    /// (R1). The grant then covers that file in each worktree of the repository. A
    /// program outside the worktree, such as <c>/usr/bin/make</c>, keeps its
    /// absolute path.
    /// </summary>
    private static ApprovalCandidate WithWorktreeProgram(ApprovalCandidate candidate, string repository)
    {
        if (candidate.Shell != ApprovalShell.Bash
            || candidate.VerbTokens is not { Count: > 0 } tokens
            || !ApprovalPatternMatching.TryGetWorktreeProgram(tokens[0], repository, out var worktreeProgram))
        {
            return candidate;
        }

        // The display verb quotes a word with whitespace (ShellCommandWordText).
        var program = ShellCommandWordText.Quote(ApprovalShell.Bash, tokens[0]);
        return candidate with
        {
            Verb = candidate.Verb.StartsWith(program, StringComparison.Ordinal)
                ? ShellCommandWordText.Quote(ApprovalShell.Bash, worktreeProgram) + candidate.Verb[program.Length..]
                : candidate.Verb,
            VerbTokens = Array.AsReadOnly([worktreeProgram, .. tokens.Skip(1)]),
        };
    }
}
