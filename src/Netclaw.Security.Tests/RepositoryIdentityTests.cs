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

public sealed class RepositoryIdentityTests : IDisposable
{
    private readonly DirectoryInfo _root = CreateTestRoot("netclaw-repository-identity-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Candidate_scopes_require_one_registered_repository()
    {
        var root = _root;
        var checkoutA = Path.Combine(root.FullName, "checkout-a");
        var worktreeA = Path.Combine(root.FullName, "worktree-a");
        var checkoutB = Path.Combine(root.FullName, "checkout-b");
        var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
        await RunGit(root.FullName, "init", checkoutA);
        await RunGit(checkoutA, "worktree", "add", "--orphan", "-b", "work-a", worktreeA);
        await RunGit(root.FullName, "init", checkoutB);

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

    [Fact]
    public async Task Registered_sibling_uses_the_same_repository_identity()
    {
        var root = _root;
        var main = Path.Combine(root.FullName, "main");
        var sibling = Path.Combine(root.FullName, "sibling");
        await RunGit(root.FullName, "init", main);
        await RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);

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
        Assert.Equal(0, await RunGitExitCode(sibling, "rev-parse", "--show-toplevel"));

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
        Assert.NotEqual(0, await RunGitExitCode(forged.FullName, "rev-parse", "--show-toplevel"));
        File.WriteAllText(Path.Combine(forgedAdmin.FullName, "HEAD"), "invalid\n");
        Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, forged.FullName, out _));
        Assert.NotEqual(0, await RunGitExitCode(forged.FullName, "rev-parse", "--show-toplevel"));

        var moved = Path.Combine(root.FullName, "moved");
        Directory.Move(sibling, moved);
        Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, moved, out _));
    }

    [Fact]
    public async Task Repository_scope_rejects_an_external_symbolic_link()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = _root;
        var main = Path.Combine(root.FullName, "main");
        var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
        await RunGit(root.FullName, "init", main);

        var alias = Path.Combine(root.FullName, "alias");
        Directory.CreateSymbolicLink(alias, main);
        Assert.False(RepositoryIdentity.TryResolve(candidateDirectory: null, alias, out _));

        Directory.CreateSymbolicLink(Path.Combine(main, "external"), outside.FullName);
        Assert.False(RepositoryIdentity.TryResolve(
            "external/marker",
            main,
            out _));
    }

    [Fact]
    public async Task Worktree_pointer_rejects_a_link_removed_by_parent_normalization()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = _root;
        var main = Path.Combine(root.FullName, "main");
        var sibling = Path.Combine(root.FullName, "sibling");
        var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "outside"));
        await RunGit(root.FullName, "init", main);
        await RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);

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

    private static async Task RunGit(string directory, params string[] arguments)
    {
        var result = await RunGitProcess(directory, arguments);
        Assert.True(result.ExitCode == 0,
            $"git exited with {result.ExitCode}: {result.StandardOutput}\n{result.StandardError}");
    }

    private static DirectoryInfo CreateTestRoot(string prefix)
        => Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"{prefix}{Guid.NewGuid():N}"));

    private static async Task<int> RunGitExitCode(string directory, params string[] arguments)
        => (await RunGitProcess(directory, arguments)).ExitCode;

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitProcess(
        string directory, string[] arguments)
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

        return await RunProcessAsync(process, TestContext.Current.CancellationToken);
    }

    internal static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        Process process, CancellationToken cancellationToken)
    {
        if (!process.StartInfo.RedirectStandardOutput || !process.StartInfo.RedirectStandardError)
            throw new ArgumentException("The test process must redirect both output streams.", nameof(process));
        Assert.True(process.Start(), $"Could not start {process.StartInfo.FileName}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        var drains = Task.WhenAll(standardOutput, standardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await drains.WaitAsync(deadline.Token);
            return (process.ExitCode, await standardOutput, await standardError);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception>
            {
                deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? new TimeoutException("The process exceeded the ten-second operation deadline.", failure)
                    : failure
            };
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // The process can exit between the ownership check and termination.
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception teardownFailure)
            {
                failures.Add(teardownFailure);
            }

            // Stop pipe reads after teardown. Their errors must not replace the operation failure.
            try
            {
                await deadline.CancelAsync();
            }
            catch (Exception cancellationFailure)
            {
                failures.Add(cancellationFailure);
            }
            try
            {
                await drains;
            }
            catch (Exception drainFailure)
            {
                failures.Add(drains.Exception is { } drainErrors ? drainErrors : drainFailure);
            }
            var output = standardOutput.IsCompletedSuccessfully ? await standardOutput : "<output did not complete>";
            var error = standardError.IsCompletedSuccessfully ? await standardError : "<error output did not complete>";
            throw new AggregateException(
                $"Process failed: {process.StartInfo.FileName} {string.Join(' ', process.StartInfo.ArgumentList)}\n" +
                $"stdout: {output}\nstderr: {error}", failures);
        }
    }
}
