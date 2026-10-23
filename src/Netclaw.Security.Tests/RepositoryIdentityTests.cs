// -----------------------------------------------------------------------
// <copyright file="RepositoryIdentityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Filesystem;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class RepositoryIdentityTests
{
    [Fact]
    public void Candidate_scopes_require_one_registered_repository()
    {
        var root = CreateTestRoot("repository-scope-fixture-");
        try
        {
            var checkoutA = Path.Combine(root.FullName, "checkout-a");
            var worktreeA = Path.Combine(root.FullName, "worktree-a");
            var checkoutB = Path.Combine(root.FullName, "checkout-b");
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
            RunGit(root.FullName, "init", checkoutA);
            RunGit(checkoutA, "worktree", "add", "--orphan", "-b", "work-a", worktreeA);
            RunGit(root.FullName, "init", checkoutB);

            var checkoutDirectory = Directory.CreateDirectory(
                Path.Combine(checkoutA, "tasks")).FullName;
            var worktreeDirectory = Directory.CreateDirectory(
                Path.Combine(worktreeA, "tasks")).FullName;
            var otherDirectory = Directory.CreateDirectory(
                Path.Combine(checkoutB, "tasks")).FullName;

            string?[] siblingCandidates = [checkoutDirectory, worktreeDirectory];
            Assert.True(RepositoryIdentity.TryResolveAll(
                siblingCandidates, session, out var siblingScopes));
            Assert.Equal(2, siblingScopes!.Count);
            Assert.Equal(checkoutA, siblingScopes[0].WorktreeRoot);
            Assert.Equal(worktreeA, siblingScopes[1].WorktreeRoot);
            Assert.True(PathUtility.AreEquivalentPaths(
                siblingScopes[0].CommonDirectory, siblingScopes[1].CommonDirectory));

            string?[] cwdFallbackCandidates = [null, worktreeDirectory];
            Assert.True(RepositoryIdentity.TryResolveAll(
                cwdFallbackCandidates, checkoutDirectory, out _));
            Assert.False(RepositoryIdentity.TryResolveAll(
                cwdFallbackCandidates, session, out _));
            Assert.False(RepositoryIdentity.TryResolveAll(
                [checkoutDirectory, otherDirectory],
                session,
                out _));
            Assert.False(RepositoryIdentity.TryResolveAll([], checkoutA, out _));
            Assert.False(RepositoryIdentity.TryResolve("tasks", cwd: null, out _));
            Assert.False(RepositoryIdentity.TryResolve(" ", checkoutA, out _));
            Assert.False(RepositoryIdentity.TryResolve(
                Path.Combine(root.FullName, "missing"), session, out _));
            Assert.False(RepositoryIdentity.TryResolveAll(
                [Path.Combine(checkoutDirectory, "..")],
                session,
                out _));

            if (!OperatingSystem.IsWindows())
            {
                var alias = Path.Combine(root.FullName, "linked-worktree");
                Directory.CreateSymbolicLink(alias, worktreeA);
                Assert.False(RepositoryIdentity.TryResolveAll([alias], session, out _));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Registered_sibling_uses_the_same_repository_identity()
    {
        var root = CreateTestRoot("netclaw-repository-grant-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            var sibling = Path.Combine(root.FullName, "sibling");
            RunGit(root.FullName, "init", main);
            RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);

            Assert.True(RepositoryIdentity.TryResolve(candidateDirectory: null, main, out var mainScope));
            Assert.True(RepositoryIdentity.TryResolve(candidateDirectory: null, sibling, out var siblingScope));
            Assert.Equal(mainScope!.CommonDirectory, siblingScope!.CommonDirectory);

            var grant = ApprovalEntry.CreateRepositoryTokenPrefix(
                ApprovalShell.Bash, ["./scripts/bump-version.sh"], mainScope.CommonDirectory);
            var folder = ApprovalEntry.CreateTokenPrefix(
                ApprovalShell.Bash, ["./scripts/bump-version.sh"], main);
            var candidate = new ApprovalCandidate("./scripts/bump-version.sh", null)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = ["./scripts/bump-version.sh"],
            };
            Assert.False(ApprovalPatternMatching.MatchesShellApproval(candidate, sibling, [folder]));
            Assert.True(ApprovalPatternMatching.MatchesShellApproval(candidate, sibling, [grant]));
            var nested = Directory.CreateDirectory(Path.Combine(sibling, "nested")).FullName;
            Assert.True(RepositoryIdentity.TryResolve(candidateDirectory: null, nested, out var nestedScope));
            Assert.Equal(mainScope.CommonDirectory, nestedScope!.CommonDirectory);
            Assert.True(ApprovalPatternMatching.MatchesShellApproval(candidate, nested, [grant]));

            File.WriteAllText(
                Path.Combine(main, ".git", "worktrees", "sibling", "commondir"),
                "../../\n");
            Assert.True(RepositoryIdentity.TryResolve(candidateDirectory: null, sibling, out var trailingScope));
            Assert.Equal(mainScope.CommonDirectory, trailingScope!.CommonDirectory);
            Assert.Equal(0, RunGitExitCode(sibling, "rev-parse", "--show-toplevel"));

            var forged = Directory.CreateDirectory(Path.Combine(root.FullName, "forged"));
            var forgedPointer = Path.Combine(forged.FullName, ".git");
            File.WriteAllText(forgedPointer, File.ReadAllText(Path.Combine(sibling, ".git")));
            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, forged.FullName, out _));

            var forgedAdmin = Directory.CreateDirectory(
                Path.Combine(main, ".git", "worktrees", "forged"));
            File.WriteAllText(Path.Combine(forgedAdmin.FullName, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(forgedAdmin.FullName, "gitdir"),
                Path.Combine(forged.FullName, ".git") + "\n");
            File.WriteAllText(forgedPointer,
                $"gitdir: {forgedAdmin.FullName}\n");
            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, forged.FullName, out _));
            Assert.NotEqual(0, RunGitExitCode(forged.FullName, "rev-parse", "--show-toplevel"));
            File.WriteAllText(Path.Combine(forgedAdmin.FullName, "HEAD"), "invalid\n");
            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, forged.FullName, out _));
            Assert.NotEqual(0, RunGitExitCode(forged.FullName, "rev-parse", "--show-toplevel"));

            var moved = Path.Combine(root.FullName, "moved");
            Directory.Move(sibling, moved);
            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, moved, out _));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A repository verb grant covers a command whose only path is the null
    /// device. It does not cover the same command when a redirect names a real
    /// file outside the repository. The null device writes no file, so it gives
    /// the candidate no scope and the cwd half of the grant applies.
    /// </summary>
    [Fact]
    public void Repository_verb_grant_keeps_the_null_device_outside_the_scope()
    {
        var root = CreateTestRoot("netclaw-repository-redirect-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            RunGit(root.FullName, "init", main);
            Assert.True(RepositoryIdentity.TryResolve(candidateDirectory: null, main, out var identity));

            var grant = ApprovalEntry.CreateRepositoryTokenPrefix(
                ApprovalShell.Bash, ["uv", "export"], identity!.CommonDirectory);

            var nullDevice = new ApprovalCandidate("uv export", Directory: null)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = ["uv", "export"],
            };
            var outsideFile = nullDevice with { Directory = Path.Combine(root.FullName, "audit") };

            Assert.True(ApprovalPatternMatching.MatchesShellApproval(nullDevice, main, [grant]));
            Assert.False(ApprovalPatternMatching.MatchesShellApproval(outsideFile, main, [grant]));

            var evaluation = ApprovalPatternMatching.EvaluateShellApproval(
                outsideFile, main, [grant], maximumNearMisses: 1);
            Assert.Null(evaluation.MatchedEntry);
            Assert.Equal(
                ShellApprovalNearMissReason.OutsideDirectory,
                Assert.Single(evaluation.NearMisses).Reason);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Repository_scope_rejects_an_external_symbolic_link()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTestRoot("netclaw-repository-link-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
            RunGit(root.FullName, "init", main);

            var alias = Path.Combine(root.FullName, "alias");
            Directory.CreateSymbolicLink(alias, main);
            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, alias, out _));

            Directory.CreateSymbolicLink(Path.Combine(main, "external"), outside.FullName);
            Assert.False(RepositoryIdentity.TryResolve(
                "external/marker",
                main,
                out _));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Worktree_pointer_rejects_a_link_removed_by_parent_normalization()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTestRoot("netclaw-repository-pointer-link-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            var sibling = Path.Combine(root.FullName, "sibling");
            var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
            RunGit(root.FullName, "init", main);
            RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);

            Directory.CreateSymbolicLink(Path.Combine(main, "link"), outside.FullName);
            var deceptivePointer = Path.Combine(
                main, "link", "..", ".git", "worktrees", "sibling");
            File.WriteAllText(Path.Combine(sibling, ".git"), $"gitdir: {deceptivePointer}\n");

            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, sibling, out _));

            Directory.CreateSymbolicLink(
                Path.Combine(main, "dangling"), Path.Combine(root.FullName, "missing"));
            var danglingPointer = Path.Combine(
                main, "dangling", "..", ".git", "worktrees", "sibling");
            File.WriteAllText(Path.Combine(sibling, ".git"), $"gitdir: {danglingPointer}\n");

            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, sibling, out _));

            File.WriteAllText(Path.Combine(main, "marker"), "file");
            var filePointer = Path.Combine(
                main, "marker", "..", ".git", "worktrees", "sibling");
            File.WriteAllText(Path.Combine(sibling, ".git"), $"gitdir: {filePointer}\n");

            Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, sibling, out _));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void RunGit(string directory, params string[] arguments)
        => Assert.Equal(0, RunGitExitCode(directory, arguments));

    private static DirectoryInfo CreateTestRoot(string prefix)
        => Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"{prefix}{Guid.NewGuid():N}"));

    private static int RunGitExitCode(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        // Do not put a deadline here. On a loaded Windows runner, "git worktree
        // add" can take more than 10 s. A deadline left git alive with its cwd
        // in the fixture, so the cleanup delete failed and hid the timeout.
        // The CI blame-hang timeout aborts a run in which git never exits.
        process.WaitForExit();
        return process.ExitCode;
    }
}
