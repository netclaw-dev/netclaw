// -----------------------------------------------------------------------
// <copyright file="RepositoryWorktreeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class RepositoryWorktreeApprovalTests(ShellApprovalMatrixFixture fixture)
{
    [Fact]
    public async Task Assignment_repository_grant_requires_the_same_assignment_in_a_registered_sibling()
    {
        var root = CreateTestRoot("repository-assignment-fixture-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            var sibling = Path.Combine(root.FullName, "sibling");
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session"));
            RunGit(root.FullName, "init", main);
            RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);

            var mainTasks = Directory.CreateDirectory(Path.Combine(main, "tasks")).FullName;
            var siblingTasks = Directory.CreateDirectory(Path.Combine(sibling, "tasks")).FullName;
            await using var promptHarness = await CreateHarnessAsync(
                "repository-assignment-prompt",
                main,
                main,
                session.FullName,
                CreateAssignedPathCommand("release", Path.Combine(mainTasks, "output.txt")),
                Approvals.None,
                AssignmentTestHost);
            var promptDecision = await promptHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, promptDecision.Outcome);
            Assert.False(
                promptDecision.ApprovalContext!.IsMessy,
                string.Join(", ", promptDecision.ApprovalContext.Candidates!.Select(
                    candidate => $"{candidate.Verb}:{candidate.Directory}:{candidate.AssignmentDigest}")));
            Assert.Contains(
                promptDecision.ApprovalContext.Options,
                option => option.Key.Value ==
                          Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveAssignmentRepositoryV1);
            var promptCandidate = Assert.Single(promptDecision.ApprovalContext.Candidates!);
            Assert.NotNull(promptCandidate.AssignmentDigest);

            var grantScope = ApprovalGrantScope.FromDecision(
                ApprovalDecision.ApprovedRepository,
                main,
                session.FullName,
                promptDecision.ApprovalContext.RepositoryCommonDirectory);
            var repositoryGrant = Assert.Single(ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, grantScope));
            Assert.Equal(promptCandidate.AssignmentDigest, repositoryGrant.Candidate.AssignmentDigest);
            Assert.Equal(Path.Combine(main, ".git"), repositoryGrant.Repository);
            Assert.Equal(main, repositoryGrant.RepositoryWorktree);
            Assert.True(ToolApprovalActor.TryCreateEntries(
                new ToolName(ShellTool.ToolName),
                [repositoryGrant],
                out var entries,
                out _));
            var entry = Assert.Single(entries);
            Assert.Equal(promptCandidate.AssignmentDigest, entry.AssignmentDigest);
            Assert.Equal(repositoryGrant.Repository, entry.Repository);

            await using var siblingHarness = await CreateHarnessAsync(
                "repository-assignment-sibling",
                sibling,
                main,
                session.FullName,
                CreateAssignedPathCommand("release", Path.Combine(siblingTasks, "output.txt")),
                Approvals.None,
                AssignmentTestHost);
            var siblingDecision = await siblingHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            var siblingCandidate = Assert.Single(siblingDecision.ApprovalContext!.Candidates!);
            Assert.Equal(promptCandidate.AssignmentDigest, siblingCandidate.AssignmentDigest);
            Assert.True(ApprovalPatternMatching.MatchesShellApproval(
                siblingCandidate,
                sibling,
                entries));

            await using var changedHarness = await CreateHarnessAsync(
                "repository-assignment-changed",
                sibling,
                main,
                session.FullName,
                CreateAssignedPathCommand("debug", Path.Combine(siblingTasks, "output.txt")),
                Approvals.None,
                AssignmentTestHost);
            var changedDecision = await changedHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            var changedCandidate = Assert.Single(changedDecision.ApprovalContext!.Candidates!);
            Assert.NotEqual(promptCandidate.AssignmentDigest, changedCandidate.AssignmentDigest);
            Assert.False(ApprovalPatternMatching.MatchesShellApproval(
                changedCandidate,
                sibling,
                entries));

            var unqualifiedCandidate = siblingCandidate with
            {
                AssignmentDigest = null,
            };
            Assert.False(ApprovalPatternMatching.MatchesShellApproval(
                unqualifiedCandidate,
                sibling,
                entries));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Repository_choice_and_reuse_follow_candidate_worktrees()
    {
        var root = CreateTestRoot("repository-candidate-fixture-");
        try
        {
            var checkoutA = Path.Combine(root.FullName, "checkout-a");
            var worktreeA = Path.Combine(root.FullName, "worktree-a");
            var checkoutB = Path.Combine(root.FullName, "checkout-b");
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session"));
            RunGit(root.FullName, "init", checkoutA);
            RunGit(checkoutA, "worktree", "add", "--orphan", "-b", "work-a", worktreeA);
            RunGit(root.FullName, "init", checkoutB);

            Directory.CreateDirectory(Path.Combine(checkoutA, "tasks"));
            Directory.CreateDirectory(Path.Combine(worktreeA, "tasks"));
            Directory.CreateDirectory(Path.Combine(checkoutB, "tasks"));

            await using var promptHarness = await CreateHarnessAsync(
                "repository-candidate-prompt",
                session.FullName,
                checkoutA,
                session.FullName,
                CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt")),
                Approvals.None);
            var promptDecision = await promptHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, promptDecision.Outcome);
            Assert.False(promptDecision.ApprovalContext!.IsMessy);
            Assert.NotNull(promptDecision.ApprovalContext.RepositoryCommonDirectory);
            Assert.True(PathUtility.AreEquivalentPaths(
                Path.Combine(checkoutA, ".git"),
                promptDecision.ApprovalContext.RepositoryCommonDirectory));
            Assert.Contains(
                promptDecision.ApprovalContext.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);

            var grantScope = ApprovalGrantScope.FromDecision(
                ApprovalDecision.ApprovedRepository,
                session.FullName,
                session.FullName,
                promptDecision.ApprovalContext.RepositoryCommonDirectory);
            var repositoryGrants = ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, grantScope);
            Assert.Single(repositoryGrants);
            Assert.All(repositoryGrants, repositoryGrant =>
            {
                Assert.Equal(Path.Combine(checkoutA, ".git"), repositoryGrant.Repository);
                Assert.Equal(worktreeA, repositoryGrant.RepositoryWorktree);
                Assert.Null(repositoryGrant.Directory);
                Assert.True(PathUtility.IsWithinRoot(
                    repositoryGrant.Candidate.Directory!, worktreeA));
            });

            await using var reuseHarness = await CreateHarnessAsync(
                "repository-candidate-reuse",
                session.FullName,
                checkoutA,
                session.FullName,
                CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt")),
                Approvals.PersistentRepository(PathCommandVerb));
            var reuseDecision = await reuseHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.Allowed, reuseDecision.Outcome);
            Assert.Equal(ToolAllowReason.StoredApproval, reuseDecision.AllowReason);

            await using var siblingCandidatesHarness = await CreateHarnessAsync(
                "repository-sibling-candidates",
                session.FullName,
                checkoutA,
                session.FullName,
                $"{CreatePathCommand(Path.Combine(checkoutA, "tasks", "output-a.txt"))}; " +
                CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt")),
                Approvals.None);
            var siblingCandidatesDecision = await siblingCandidatesHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Contains(
                siblingCandidatesDecision.ApprovalContext!.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);

            await using var mixedCandidatesHarness = await CreateHarnessAsync(
                "repository-mixed-candidates",
                session.FullName,
                checkoutA,
                session.FullName,
                $"{CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt"))}; " +
                CreatePathCommand(Path.Combine(checkoutB, "tasks", "output-c.txt")),
                Approvals.None);
            var mixedCandidatesDecision = await mixedCandidatesHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                mixedCandidatesDecision.ApprovalContext!.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);

            await using var fallbackHarness = await CreateHarnessAsync(
                "repository-cwd-fallback",
                checkoutA,
                checkoutA,
                session.FullName,
                $"git status; {CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt"))}",
                Approvals.None);
            var fallbackDecision = await fallbackHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Contains(
                fallbackDecision.ApprovalContext!.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);

            // Netclaw does not classify a PowerShell command as a pure side effect.
            if (!OperatingSystem.IsWindows())
            {
                await using var sideEffectHarness = await CreateHarnessAsync(
                    "repository-pure-side-effect",
                    session.FullName,
                    checkoutA,
                    session.FullName,
                    $"{CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt"))}; echo done",
                    Approvals.None);
                var sideEffectDecision = await sideEffectHarness.EvaluateDecisionAsync(
                    TestContext.Current.CancellationToken);
                Assert.Contains(
                    sideEffectDecision.ApprovalContext!.Options,
                    option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);
            }

            await using var redirectHarness = await CreateHarnessAsync(
                "repository-external-redirect",
                session.FullName,
                checkoutA,
                session.FullName,
                $"{CreatePathCommand(Path.Combine(worktreeA, "tasks", "output-b.txt"))}; " +
                CreateRedirectCommand(Path.Combine(session.FullName, "output.txt")),
                Approvals.None);
            var redirectDecision = await redirectHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                redirectDecision.ApprovalContext!.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);

            var taskGrant = Assert.Single(repositoryGrants,
                grant => grant.Candidate.Verb == PathCommandVerb);
            Assert.Equal(Path.Combine(worktreeA, "tasks"), taskGrant.Candidate.Directory);
            RunGit(root.FullName, "init", taskGrant.Candidate.Directory!);
            var persistenceFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                promptHarness.ApprovalService.RecordApprovalCandidatesAsync(
                    (ToolApprovalSessionId)"signalr/repository-persistence",
                    TrustAudience.Personal,
                    new ToolName(ShellTool.ToolName),
                    repositoryGrants,
                    persistent: true,
                    TestContext.Current.CancellationToken));
            Assert.Contains("InvalidData", persistenceFailure.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Repository_grant_covers_a_registered_sibling_and_keeps_other_verbs_separate()
    {
        var root = CreateTestRoot("netclaw-repository-approval-");
        try
        {
            var main = Path.Combine(root.FullName, "main");
            var sibling = Path.Combine(root.FullName, "sibling");
            var unrelated = Path.Combine(root.FullName, "unrelated");
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session"));
            RunGit(root.FullName, "init", main);
            RunGit(main, "worktree", "add", "--orphan", "-b", "sibling", sibling);
            RunGit(root.FullName, "init", unrelated);

            await using var promptHarness = await CreateHarnessAsync(
                "repository-prompt",
                sibling,
                main,
                session.FullName,
                "./scripts/bump-version.sh",
                Approvals.None);
            var promptDecision = await promptHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, promptDecision.Outcome);
            Assert.Contains(
                promptDecision.ApprovalContext!.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);
            var grantScope = ApprovalGrantScope.FromDecision(
                ApprovalDecision.ApprovedRepository, sibling, session.FullName,
                promptDecision.ApprovalContext.RepositoryCommonDirectory);
            var repositoryGrant = Assert.Single(ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, grantScope));
            Assert.Equal(Path.Combine(main, ".git"), repositoryGrant.Repository);
            Assert.Equal(sibling, repositoryGrant.RepositoryWorktree);
            var swappedGrantScope = ApprovalGrantScope.FromDecision(
                ApprovalDecision.ApprovedRepository, sibling, session.FullName,
                Path.Combine(unrelated, ".git"));
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, swappedGrantScope));
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                [new Netclaw.Security.ApprovalCandidate("touch", Path.Combine(root.FullName, "outside"))],
                grantScope));
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                [new Netclaw.Security.ApprovalCandidate("cd", Path.Combine(root.FullName, "outside")),
                    new Netclaw.Security.ApprovalCandidate("./scripts/bump-version.sh", null)],
                grantScope));

            var grants = Approvals.Combine(
                Approvals.PersistentRepository("./scripts/bump-version.sh"),
                Approvals.PersistentAnywhere("cd"));
            await using var siblingHarness = await CreateHarnessAsync(
                "repository-sibling",
                sibling,
                main,
                session.FullName,
                "./scripts/bump-version.sh",
                grants);

            var siblingDecision = await siblingHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.Allowed, siblingDecision.Outcome);
            Assert.Equal(ToolAllowReason.StoredApproval, siblingDecision.AllowReason);

            // The Windows harness uses PowerShell. This case tests a Bash compound.
            if (!OperatingSystem.IsWindows())
            {
                await using var otherVerbHarness = await CreateHarnessAsync(
                    "repository-other-verb",
                    sibling,
                    main,
                    session.FullName,
                    "cd . && ./scripts/bump-version.sh; python3 -V",
                    grants);
                var otherVerbDecision = await otherVerbHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
                Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, otherVerbDecision.Outcome);
                Assert.Contains("python3", otherVerbDecision.ApprovalContext!.CandidateVerbs);
            }

            await using var unrelatedHarness = await CreateHarnessAsync(
                "repository-unrelated",
                unrelated,
                main,
                session.FullName,
                "./scripts/bump-version.sh",
                grants);
            var unrelatedDecision = await unrelatedHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, unrelatedDecision.Outcome);

            await using var headlessHarness = await ShellApprovalHarness.CreateAsync(
                "repository-headless",
                new ShellApprovalInvocation("./scripts/bump-version.sh", ApprovalDirectoryShape.None,
                    Interactive: false),
                grants,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(sibling, session.FullName,
                    "signalr/repository-headless", [])
                { RepositoryGrantWorktree = main });
            var headlessDecision = await headlessHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.Allowed, headlessDecision.Outcome);

            await using var headlessOtherVerbHarness = await ShellApprovalHarness.CreateAsync(
                "repository-headless-other-verb",
                new ShellApprovalInvocation("python3 -V", ApprovalDirectoryShape.None,
                    Interactive: false),
                grants,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(sibling, session.FullName,
                    "signalr/repository-headless-other-verb", [])
                { RepositoryGrantWorktree = main });
            var headlessOtherVerbDecision = await headlessOtherVerbHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, headlessOtherVerbDecision.Outcome);

            await using var hardDenyHarness = await CreateHarnessAsync(
                "repository-hard-deny",
                sibling,
                main,
                session.FullName,
                "netclaw daemon stop",
                Approvals.PersistentRepository("netclaw daemon stop"));
            var hardDenyDecision = await hardDenyHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.Denied, hardDenyDecision.Outcome);
            Assert.Equal("hard_deny_self_destructive", hardDenyDecision.DenyReason);

            await using var otherAudienceHarness = await ShellApprovalHarness.CreateAsync(
                "repository-other-audience",
                new ShellApprovalInvocation("./scripts/bump-version.sh", ApprovalDirectoryShape.None,
                    TrustAudience.Team),
                grants,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(sibling, session.FullName,
                    "signalr/repository-other-audience", [])
                { RepositoryGrantWorktree = main });
            var otherAudienceDecision = await otherAudienceHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.Denied, otherAudienceDecision.Outcome);

            await using var outsidePathHarness = await CreateHarnessAsync(
                "repository-outside-path",
                sibling,
                main,
                session.FullName,
                $"touch {Path.Combine(root.FullName, "outside-file")}",
                Approvals.PersistentRepository("touch"));
            var outsidePathDecision = await outsidePathHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, outsidePathDecision.Outcome);

            var swappedAdmin = Directory.CreateDirectory(
                Path.Combine(unrelated, ".git", "worktrees", "swapped"));
            File.WriteAllText(Path.Combine(swappedAdmin.FullName, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(swappedAdmin.FullName, "gitdir"),
                Path.Combine(sibling, ".git") + "\n");
            File.WriteAllText(Path.Combine(swappedAdmin.FullName, "HEAD"),
                File.ReadAllText(Path.Combine(unrelated, ".git", "HEAD")));
            File.SetAttributes(Path.Combine(sibling, ".git"), FileAttributes.Normal);
            File.WriteAllText(Path.Combine(sibling, ".git"),
                $"gitdir: {swappedAdmin.FullName}\n");
            RunGit(sibling, "rev-parse", "--show-toplevel");
            Assert.True(Netclaw.Security.GitRepositoryApprovalScope.TryResolve(sibling, out var swappedScope));
            Assert.Equal(Path.Combine(unrelated, ".git"), swappedScope!.CommonDirectory);
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, grantScope));

            await using var changedRegistrationHarness = await CreateHarnessAsync(
                "repository-changed-registration",
                sibling,
                main,
                session.FullName,
                "./scripts/bump-version.sh",
                grants);
            var changedRegistrationDecision = await changedRegistrationHarness.EvaluateDecisionAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, changedRegistrationDecision.Outcome);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Separate_git_directory_does_not_offer_repository_scope()
    {
        var root = CreateTestRoot("netclaw-separate-git-directory-");
        try
        {
            var checkout = Path.Combine(root.FullName, "checkout");
            var metadata = Path.Combine(root.FullName, "metadata");
            var session = Directory.CreateDirectory(Path.Combine(root.FullName, "session"));
            RunGit(root.FullName, "init", "--separate-git-dir", metadata, checkout);
            Assert.False(Netclaw.Security.GitRepositoryApprovalScope.TryResolve(checkout, out _));

            await using var harness = await CreateHarnessAsync(
                "repository-separate-git-directory",
                checkout,
                checkout,
                session.FullName,
                "./scripts/bump-version.sh",
                Approvals.None);
            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            Assert.DoesNotContain(decision.ApprovalContext!.Options,
                option => option.Key.Value == Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveRepository);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        string id,
        string project,
        string grantWorktree,
        string session,
        string command,
        ApprovalState grants,
        ShellApprovalHost host = ShellApprovalHost.Bash)
        => ShellApprovalHarness.CreateAsync(
            id,
            new ShellApprovalInvocation(command, ApprovalDirectoryShape.None, Host: host),
            grants,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            scope: new ShellApprovalHarnessScope(project, session, $"signalr/{id}", [])
            {
                RepositoryGrantWorktree = grantWorktree,
            });

    private static ShellApprovalHost AssignmentTestHost => OperatingSystem.IsWindows()
        ? ShellApprovalHost.PowerShell7
        : ShellApprovalHost.Bash52;

    private static string PathCommandVerb => OperatingSystem.IsWindows()
        ? "Set-Location"
        : "touch";

    private static string CreatePathCommand(string path)
    {
        if (!OperatingSystem.IsWindows())
            return $"touch '{path.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        var directory = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
        return $"Set-Location '{directory.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string CreateAssignedPathCommand(string value, string path) =>
        OperatingSystem.IsWindows()
            ? $"$mode = '{value}'; {CreatePathCommand(path)}"
            : $"mode='{value}' {CreatePathCommand(path)}";

    private static string CreateRedirectCommand(string path) => OperatingSystem.IsWindows()
        ? $"Write-Output done > '{path.Replace("'", "''", StringComparison.Ordinal)}'"
        : $"echo done > '{path.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

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

    private static DirectoryInfo CreateTestRoot(string prefix)
        => Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"{prefix}{Guid.NewGuid():N}"));
}
