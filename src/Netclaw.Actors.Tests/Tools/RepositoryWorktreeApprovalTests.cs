// -----------------------------------------------------------------------
// <copyright file="RepositoryWorktreeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class RepositoryWorktreeApprovalTests(ShellApprovalMatrixFixture fixture)
{
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
            var grantContext = ApprovalGrantContext.FromDecision(
                ApprovalDecision.ApprovedRepository, sibling, session.FullName,
                promptDecision.ApprovalContext.RepositoryCommonDirectory);
            var repositoryGrant = Assert.Single(ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, grantContext));
            Assert.Equal(Path.Combine(main, ".git"), repositoryGrant.Repository);
            Assert.Equal(sibling, repositoryGrant.RepositoryWorktree);
            var swappedContext = ApprovalGrantContext.FromDecision(
                ApprovalDecision.ApprovedRepository, sibling, session.FullName,
                Path.Combine(unrelated, ".git"));
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                promptDecision.ApprovalContext.Candidates!, swappedContext));
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                [new Netclaw.Security.ApprovalCandidate("touch", Path.Combine(root.FullName, "outside"))],
                grantContext));
            Assert.Throws<InvalidOperationException>(() => ApprovalBucketBuilder.BuildGrants(
                [new Netclaw.Security.ApprovalCandidate("cd", Path.Combine(root.FullName, "outside")),
                    new Netclaw.Security.ApprovalCandidate("./scripts/bump-version.sh", null)],
                grantContext));

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
                    "signalr/repository-headless", []) { RepositoryGrantWorktree = main });
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
                    "signalr/repository-headless-other-verb", []) { RepositoryGrantWorktree = main });
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
                    "signalr/repository-other-audience", []) { RepositoryGrantWorktree = main });
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
                promptDecision.ApprovalContext.Candidates!, grantContext));

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
        ApprovalState grants)
        => ShellApprovalHarness.CreateAsync(
            id,
            new ShellApprovalInvocation(command, ApprovalDirectoryShape.None),
            grants,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            scope: new ShellApprovalHarnessScope(project, session, $"signalr/{id}", [])
            {
                RepositoryGrantWorktree = grantWorktree,
            });

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
