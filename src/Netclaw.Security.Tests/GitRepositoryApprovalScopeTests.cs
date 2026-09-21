// -----------------------------------------------------------------------
// <copyright file="GitRepositoryApprovalScopeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class GitRepositoryApprovalScopeTests
{
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

            Assert.True(GitRepositoryApprovalScope.TryResolve(main, out var mainScope));
            Assert.True(GitRepositoryApprovalScope.TryResolve(sibling, out var siblingScope));
            Assert.Equal(mainScope!.CommonDirectory, siblingScope!.CommonDirectory);
            Assert.True(siblingScope.Contains("scripts/bump-version.sh", sibling));
            Assert.False(siblingScope.Contains(Path.Combine(root.FullName, "outside", "marker"), sibling));

            var grant = ApprovalEntry.CreateRepositoryTokenPrefix(
                ApprovalShell.Bash, ["./scripts/bump-version.sh"], mainScope.CommonDirectory);
            Assert.False(ApprovalPatternMatching.MatchesShellApproval(
                "./scripts/bump-version.sh", null, sibling, [grant]));
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
            Assert.True(GitRepositoryApprovalScope.TryResolve(nested, out var nestedScope));
            Assert.Equal(mainScope.CommonDirectory, nestedScope!.CommonDirectory);
            Assert.True(ApprovalPatternMatching.MatchesShellApproval(candidate, nested, [grant]));

            File.WriteAllText(
                Path.Combine(main, ".git", "worktrees", "sibling", "commondir"),
                "../../\n");
            Assert.True(GitRepositoryApprovalScope.TryResolve(sibling, out var trailingScope));
            Assert.Equal(mainScope.CommonDirectory, trailingScope!.CommonDirectory);
            Assert.Equal(0, RunGitExitCode(sibling, "rev-parse", "--show-toplevel"));

            var forged = Directory.CreateDirectory(Path.Combine(root.FullName, "forged"));
            var forgedPointer = Path.Combine(forged.FullName, ".git");
            File.WriteAllText(forgedPointer, File.ReadAllText(Path.Combine(sibling, ".git")));
            Assert.False(GitRepositoryApprovalScope.TryResolve(forged.FullName, out _));

            var forgedAdmin = Directory.CreateDirectory(
                Path.Combine(main, ".git", "worktrees", "forged"));
            File.WriteAllText(Path.Combine(forgedAdmin.FullName, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(forgedAdmin.FullName, "gitdir"),
                Path.Combine(forged.FullName, ".git") + "\n");
            File.WriteAllText(forgedPointer,
                $"gitdir: {forgedAdmin.FullName}\n");
            Assert.False(GitRepositoryApprovalScope.TryResolve(forged.FullName, out _));
            Assert.NotEqual(0, RunGitExitCode(forged.FullName, "rev-parse", "--show-toplevel"));
            File.WriteAllText(Path.Combine(forgedAdmin.FullName, "HEAD"), "invalid\n");
            Assert.False(GitRepositoryApprovalScope.TryResolve(forged.FullName, out _));
            Assert.NotEqual(0, RunGitExitCode(forged.FullName, "rev-parse", "--show-toplevel"));

            var moved = Path.Combine(root.FullName, "moved");
            Directory.Move(sibling, moved);
            Assert.False(GitRepositoryApprovalScope.TryResolve(moved, out _));
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
            Assert.False(GitRepositoryApprovalScope.TryResolve(alias, out _));

            Directory.CreateSymbolicLink(Path.Combine(main, "external"), outside.FullName);
            Assert.True(GitRepositoryApprovalScope.TryResolve(main, out var scope));
            Assert.False(scope!.Contains("external/marker", main));
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

            Assert.False(GitRepositoryApprovalScope.TryResolve(sibling, out _));

            Directory.CreateSymbolicLink(
                Path.Combine(main, "dangling"), Path.Combine(root.FullName, "missing"));
            var danglingPointer = Path.Combine(
                main, "dangling", "..", ".git", "worktrees", "sibling");
            File.WriteAllText(Path.Combine(sibling, ".git"), $"gitdir: {danglingPointer}\n");

            Assert.False(GitRepositoryApprovalScope.TryResolve(sibling, out _));

            File.WriteAllText(Path.Combine(main, "marker"), "file");
            var filePointer = Path.Combine(
                main, "marker", "..", ".git", "worktrees", "sibling");
            File.WriteAllText(Path.Combine(sibling, ".git"), $"gitdir: {filePointer}\n");

            Assert.False(GitRepositoryApprovalScope.TryResolve(sibling, out _));
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
        Assert.True(process.WaitForExit(10_000), "git timed out");
        return process.ExitCode;
    }
}
