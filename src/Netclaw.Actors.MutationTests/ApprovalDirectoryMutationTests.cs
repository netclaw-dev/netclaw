// -----------------------------------------------------------------------
// <copyright file="ApprovalDirectoryMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ApprovalDirectoryMutationTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"netclaw-approval-mutations-{Guid.NewGuid():N}");
    private readonly string _grantRoot;
    private readonly string _outside;
    private readonly ApprovalShell _shell = OperatingSystem.IsWindows() ? ApprovalShell.PowerShell : ApprovalShell.Bash;

    public ApprovalDirectoryMutationTests()
    {
        _grantRoot = Path.Combine(_basePath, "app");
        _outside = Path.Combine(_basePath, "app-other");
        Directory.CreateDirectory(Path.Combine(_grantRoot, "src"));
        Directory.CreateDirectory(Path.Combine(_outside, "nested"));
    }

    [Theory]
    [InlineData(".", true)]
    [InlineData("src", true)]
    [InlineData("src/../src", true)]
    [InlineData("../app-other", false)]
    [InlineData("src/../../app-other", false)]
    public void Folder_grant_requires_normalized_containment(string relativePath, bool allowed)
    {
        var candidate = Path.Combine(_grantRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(allowed, Matches(candidate, _grantRoot));
    }

    [Fact]
    public void Candidate_directory_owns_scope_even_when_cwd_disagrees()
    {
        Assert.False(Matches(_outside, _grantRoot));
        Assert.True(Matches(Path.Combine(_grantRoot, "src"), _outside));
    }

    [Fact]
    public void Relative_scope_uses_cwd_without_widening_the_grant()
    {
        Assert.False(Matches("../app-other", _grantRoot));
        Assert.True(Matches("src", _grantRoot));
        Assert.False(Matches(null, _outside));
        Assert.True(Matches(null, Path.Combine(_grantRoot, "src")));
        Assert.False(Matches(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Folder_grant_rejects_a_link_to_a_sibling_directory(bool nested)
    {
        var link = Path.Combine(_grantRoot, "link");
        var linkInfo = Directory.CreateSymbolicLink(link, _outside);
        Assert.Equal(_outside, linkInfo.ResolveLinkTarget(returnFinalTarget: true)!.FullName);
        var candidate = nested ? Path.Combine(link, "nested") : link;

        Assert.False(Matches(candidate, _grantRoot));
        Assert.True(Matches(Path.Combine(_grantRoot, "src"), _grantRoot));
    }

    [Fact]
    public void Folder_grant_trusts_a_link_at_its_own_root()
    {
        // A folder grant refuses links only below its root (R3). The operator
        // approved the root by name, and an OS alias such as macOS /tmp can be it.
        var alias = Path.Combine(_basePath, "app-alias");
        Directory.CreateSymbolicLink(alias, _grantRoot);
        var grant = ApprovalEntry.CreateTokenPrefix(_shell, ["git", "status"], alias);

        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            CreateCandidate(_shell, Path.Combine(alias, "src")), alias, [grant]));
    }

    [Fact]
    public void Folder_grant_compares_posix_paths_with_case()
    {
        // R4: allow checks keep case on POSIX hosts, macOS included.
        if (OperatingSystem.IsWindows())
            return;

        Assert.False(Matches(_grantRoot.ToUpperInvariant(), _grantRoot));
        Assert.False(Matches(Path.Combine(_grantRoot, "src").ToUpperInvariant(), _grantRoot));
        Assert.True(Matches(Path.Combine(_grantRoot, "src"), _grantRoot));
    }

    // The OS follows the link before it applies "..", so link/../notes.txt
    // names app-other/notes.txt. Its lexical form stays inside the grant. A
    // link above the segment that ".." leaves, such as a root alias, is safe.
    [Fact]
    public void Folder_grant_does_not_cover_a_parent_segment_after_a_link()
    {
        Directory.CreateSymbolicLink(Path.Combine(_grantRoot, "link"), Path.Combine(_outside, "nested"));
        var alias = Path.Combine(_basePath, "app-alias");
        Directory.CreateSymbolicLink(alias, _grantRoot);
        var matcher = new ShellApprovalMatcher(OperatingSystem.IsWindows()
            ? ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                PwshDialect.PowerShell7)
            : ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        var verb = OperatingSystem.IsWindows() ? "Get-Content" : "cat";
        Assert.True(CommandMatches(_grantRoot, Path.Combine(_grantRoot, "src", "..", "notes.txt")));
        Assert.True(CommandMatches(alias, Path.Combine(alias, "src", "..", "notes.txt")));
        Assert.False(CommandMatches(_grantRoot, Path.Combine(_grantRoot, "link", "..", "notes.txt")));

        bool CommandMatches(string root, string path)
        {
            var grant = ApprovalEntry.CreateTokenPrefix(_shell, [verb], root);
            var analysis = matcher.AnalyzeInvocation(
                new ToolName(ShellTool.ToolName),
                new Dictionary<string, object?>
                {
                    ["Command"] = $"{verb} '{path}'",
                    ["WorkingDirectory"] = root,
                });
            return !analysis.IsMessy
                   && analysis.Candidates.Count > 0
                   && analysis.Candidates.All(candidate =>
                       ApprovalPatternMatching.MatchesShellApproval(candidate, root, [grant]));
        }
    }

    [Theory]
    [InlineData(@"C:\repo\app", true)]
    [InlineData(@"c:\REPO\APP\src", true)]
    [InlineData(@"C:\repo\app-other", false)]
    [InlineData(@"C:\repo\app\..\app-other", false)]
    [InlineData(@"D:\repo\app\src", false)]
    [InlineData(@"..\app-other", false)]
    [InlineData(@"\repo\app\src", false)]
    [InlineData(@"~\src", false)]
    public void PowerShell_scope_preserves_windows_path_boundaries(string directory, bool allowed)
    {
        var grant = ApprovalEntry.CreateTokenPrefix(ApprovalShell.PowerShell, ["git", "status"], @"C:\repo\app");
        var candidate = CreateCandidate(ApprovalShell.PowerShell, directory);

        Assert.Equal(allowed, ApprovalPatternMatching.MatchesShellApproval(candidate, @"C:\repo\app", [grant]));
    }

    [Fact]
    public void Repository_grant_requires_matching_identity_and_a_path_inside_the_worktree()
    {
        var main = Path.Combine(_basePath, "main");
        var sibling = Path.Combine(_basePath, "sibling");
        var unrelated = Path.Combine(_basePath, "unrelated");
        RunGit(_basePath, "init", main);
        RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);
        RunGit(_basePath, "init", unrelated);

        var grant = ApprovalEntry.CreateRepositoryTokenPrefix(
            ApprovalShell.Bash, ["git", "status"], Path.Combine(main, ".git"));
        var otherRepositoryGrant = grant with { Repository = Path.Combine(unrelated, ".git") };
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            CreateCandidate(ApprovalShell.Bash, null), sibling, [grant]));
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            CreateCandidate(ApprovalShell.Bash, sibling), _outside, [grant]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            CreateCandidate(ApprovalShell.Bash, null), sibling, [otherRepositoryGrant]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            CreateCandidate(ApprovalShell.Bash, _outside), sibling, [grant]));
        Assert.False(RepositoryIdentity.TryResolve("relative", cwd: null, out _));

        Assert.True(RepositoryIdentity.TryResolveAll([main, sibling], _outside, out _));
        Assert.False(RepositoryIdentity.TryResolveAll([main, unrelated], _outside, out _));
    }

    [Fact]
    public void Repository_persistence_revalidates_the_candidate_worktree()
    {
        var main = Path.Combine(_basePath, "persistence-main");
        var sibling = Path.Combine(_basePath, "persistence-sibling");
        var unrelated = Path.Combine(_basePath, "persistence-unrelated");
        var candidateDirectory = Path.Combine(sibling, "tasks");
        RunGit(_basePath, "init", main);
        RunGit(main, "worktree", "add", "--orphan", "-b", "persistence-sibling", sibling);
        RunGit(_basePath, "init", unrelated);
        Directory.CreateDirectory(candidateDirectory);

        var candidate = CreateCandidate(ApprovalShell.Bash, candidateDirectory);
        var grant = new ToolApprovalGrant(candidate, new GrantScope.Repository(Path.Combine(main, ".git")))
        {
            RepositoryWorktree = sibling,
        };
        Assert.True(ToolApprovalActor.TryCreateEntries(
            new ToolName(ShellTool.ToolName), [grant], out var persistent, out _));
        Assert.Single(persistent);

        var wrongIdentity = grant with { Scope = new GrantScope.Repository(Path.Combine(unrelated, ".git")) };
        Assert.False(ToolApprovalActor.TryCreateEntries(
            new ToolName(ShellTool.ToolName), [wrongIdentity], out _, out _));
        var wrongRoot = grant with { RepositoryWorktree = main };
        Assert.False(ToolApprovalActor.TryCreateEntries(
            new ToolName(ShellTool.ToolName), [wrongRoot], out _, out _));
        var missingCandidate = grant with
        {
            Candidate = candidate with
            {
                Directory = Path.Combine(sibling, "missing"),
            },
        };
        Assert.False(ToolApprovalActor.TryCreateEntries(
            new ToolName(ShellTool.ToolName), [missingCandidate], out _, out _));

        Directory.Delete(candidateDirectory);
        RunGit(main, "worktree", "add", "--orphan", "-b", "nested-candidate", candidateDirectory);
        Assert.True(RepositoryIdentity.TryResolve(
            candidateDirectory, cwd: null, out var nestedScope));
        Assert.True(PathUtility.AreEquivalentPaths(
            nestedScope!.CommonDirectory, ((GrantScope.Repository)grant.Scope).CommonDirectory));
        Assert.False(PathUtility.AreEquivalentPaths(
            nestedScope.WorktreeRoot, grant.RepositoryWorktree));
        Assert.False(ToolApprovalActor.TryCreateEntries(
            new ToolName(ShellTool.ToolName), [grant], out _, out _));
    }

    [Fact]
    public void Folder_grant_uses_the_directory_where_each_occurrence_runs()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var policy = new ShellCommandPolicy(environment);
        var analysis = policy.Analyze("cd /work/sub && inspect; cat *.md", "/work");
        Assert.True(BashDirectoryScopeProjection.TryCreate(
            analysis,
            policy,
            new ShellApprovalMatcher(environment),
            out var projection));

        var grants = GrantBuilder.Build(
            projection.Candidates,
            GrantScopeKind.Folder,
            "/work",
            "/session",
            repositoryCommonDirectory: null);
        // A candidate without its own directory uses the call directory, not everywhere.
        var bare = Assert.Single(GrantBuilder.Build(
            [CreateCandidate(ApprovalShell.Bash, directory: null)],
            GrantScopeKind.Folder,
            "/work",
            "/session",
            repositoryCommonDirectory: null));

        // After cd, a folder grant never falls back to the session working directory.
        Assert.Equal(
            ["cd@/work/sub", "inspect@/work/sub", "cat@/work", "cat@/work/sub"],
            grants.Select(static grant =>
                $"{grant.Candidate.Verb}@{Assert.IsType<GrantScope.Folder>(grant.Scope).Directory}"));
        Assert.Equal("/work", Assert.IsType<GrantScope.Folder>(bare.Scope).Directory);
    }

    public void Dispose() => Directory.Delete(_basePath, recursive: true);

    private bool Matches(string? directory, string? cwd)
    {
        var grant = ApprovalEntry.CreateTokenPrefix(_shell, ["git", "status"], _grantRoot);
        return ApprovalPatternMatching.MatchesShellApproval(CreateCandidate(_shell, directory), cwd, [grant]);
    }

    private static ApprovalCandidate CreateCandidate(ApprovalShell shell, string? directory) =>
        new("git status", directory)
        {
            Shell = shell,
            VerbTokens = Array.AsReadOnly(["git", "status"])
        };

    private static void RunGit(string directory, params string[] arguments)
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
        Assert.Equal(0, process.ExitCode);
    }
}
