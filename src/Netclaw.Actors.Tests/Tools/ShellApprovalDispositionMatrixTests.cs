// -----------------------------------------------------------------------
// <copyright file="ShellApprovalDispositionMatrixTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellApprovalDispositionMatrixTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "These rows require a POSIX filesystem in addition to the explicitly selected Bash grammar.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Bash matrix rows require POSIX filesystem semantics.")]
    [MemberData(nameof(ShellApprovalCases.BashRows), MemberType = typeof(ShellApprovalCases))]
    public Task Bash_approval_contract(string caseId)
        => AssertApprovalContract(caseId);

    [Theory]
    [MemberData(nameof(ShellApprovalCases.PowerShellRows), MemberType = typeof(ShellApprovalCases))]
    public Task Power_shell_approval_contract(string caseId)
        => AssertApprovalContract(caseId);

    private async Task AssertApprovalContract(string caseId)
    {
        await AssertApprovalContract(ShellApprovalCases.Get(caseId));
    }

    private async Task AssertApprovalContract(ShellApprovalCase testCase)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(testCase.Expected.Outcome, observed.Outcome);
        Assert.Equal(testCase.Expected.AllowReason, observed.AllowReason);
        Assert.Equal(testCase.Expected.DenyReason, observed.DenyReason);
        Assert.Equal(testCase.Expected.Candidates, observed.CandidateVerbs);
        Assert.Equal(testCase.Expected.IsMessy, observed.IsMessy);
        Assert.Equal(testCase.Expected.ApprovalChecks, harness.ApprovalService.CheckCount);
        Assert.Equal(testCase.Expected.ApprovalMatches, observed.ApprovalMatches);
    }

    [Fact]
    public Task Interactive_reviewed_safe_candidate_uses_reviewed_policy()
    {
        var invocation = OperatingSystem.IsWindows()
            ? new ShellApprovalInvocation(
                "Get-Date",
                Host: ShellApprovalHost.PowerShell7)
            : new ShellApprovalInvocation("git status");
        return AssertApprovalContract(new ShellApprovalCase(
            "interactive-reviewed-safe-allows",
            invocation,
            Approvals.None,
            ExpectedApproval.Allow(ToolAllowReason.ReviewedSafePolicy)));
    }

    [Fact]
    public Task Noninteractive_reviewed_safe_candidate_stays_uncovered()
        => AssertApprovalContract(new ShellApprovalCase(
            "noninteractive-reviewed-safe-requires-approval",
            new ShellApprovalInvocation("git status", Interactive: false),
            Approvals.None,
            ExpectedApproval.Require(["git status"])));

    [Fact]
    public Task Noninteractive_candidate_can_use_an_explicit_persistent_grant()
        => AssertApprovalContract(new ShellApprovalCase(
            "noninteractive-reviewed-safe-with-grant-allows",
            new ShellApprovalInvocation("git status", Interactive: false),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Allow(
                ToolAllowReason.StoredApproval,
                1,
                "persistent:git status")));

    [SlopwatchSuppress("SW001", "The command uses a POSIX Bash status parameter.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Unquoted_status_output_offers_reusable_grant_for_unapproved_verb()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            ShellApprovalCases.Get("unquoted-status-output-prompts-for-unapproved-verb"),
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.False(approval.IsMessy);
        Assert.Equal(["git push"], approval.CandidateVerbs);
        Assert.Contains(
            approval.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveSession);
    }

    [SlopwatchSuppress("SW001", "The observed compound uses POSIX Bash directory and pipeline semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Declared_project_does_not_resolve_an_inline_directory_pipeline()
    {
        var testCase = new ShellApprovalCase(
            "declared-project-inline-directory-pipeline",
            new ShellApprovalInvocation(
                "cd sub && cat result.txt | sed -n '1p'; ls .",
                ApprovalDirectoryShape.None),
            Approvals.PersistentAnywhere("cd", "cat", "sed", "ls"),
            ExpectedApproval.Require([], isMessy: true, approvalChecks: 0));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("sub");

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.True(decision.ApprovalContext?.IsMessy);
        Assert.Empty(decision.ApprovalContext!.CandidateVerbs);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [SlopwatchSuppress("SW001", "This case requires POSIX Bash directory and pipeline semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Complete_static_compound_uses_grants_for_each_reachable_scope()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-static-shell-scopes-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            var command = $"cd {child.FullName} && cat result.txt | sed -n '1p'; ls .";
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "static-shell-scopes",
                new ShellApprovalInvocation(
                    command,
                    ApprovalDirectoryShape.None),
                Approvals.PersistentAnywhere("cd", "cat", "sed", "ls"),
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/static-shell-scopes",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
            Assert.Equal(ToolAllowReason.StoredApproval, decision.AllowReason);

            await using var missingStage = await ShellApprovalHarness.CreateAsync(
                "static-shell-missing-stage",
                new ShellApprovalInvocation(command, ApprovalDirectoryShape.None),
                Approvals.PersistentAnywhere("cd", "cat", "ls"),
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/static-shell-missing-stage",
                    []));
            var missingDecision = await missingStage.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, missingDecision.Outcome);
            Assert.False(missingDecision.ApprovalContext?.IsMessy);
            Assert.Equal(["sed"], missingDecision.ApprovalContext?.CandidateVerbs);
            Assert.Contains(
                missingDecision.ApprovalContext!.Options,
                option => option.Key.Value == ApprovalOptionKeys.ApproveSession);
            missingStage.SeedOneTimeApproval(missingDecision.ApprovalContext!);
            var retryDecision = await missingStage.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.Allowed, retryDecision.Outcome);
            Assert.Equal(ToolAllowReason.OneTimeApproval, retryDecision.AllowReason);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX symlink semantics.")]
    [InlineData("touch marker.txt")]
    [InlineData("cd absent || touch marker.txt")]
    public async Task Shell_policy_rejects_a_lexical_directory_after_a_symlink_parent(string command)
    {
        var root = Directory.CreateTempSubdirectory("netclaw-static-shell-link-parent-");
        try
        {
            var target = root.CreateSubdirectory("outside").CreateSubdirectory("inner");
            var project = root.CreateSubdirectory("project");
            Directory.CreateSymbolicLink(Path.Combine(project.FullName, "alias"), target.FullName);
            var rawDirectory = Path.Combine(project.FullName, "alias", "..");
            var grants = Approvals.PersistentAnywhere("cd", "touch");
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "static-shell-link-parent",
                new ShellApprovalInvocation(command, ApprovalDirectoryShape.None),
                grants,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    rawDirectory,
                    project.FullName,
                    "signalr/static-shell-link-parent",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
            Assert.Equal("shell_invalid_working_directory", decision.DenyReason);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "A failed Bash directory change leaves the later command in its initial scope.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Child_grant_does_not_cover_the_failed_directory_change_path()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-static-shell-failure-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            var grants = Approvals.Combine(
                Approvals.PersistentAnywhere("cd", "cat", "sed"),
                Approvals.PersistentHere(ApprovalDirectoryShape.ProjectChild, "touch"));
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "static-shell-failed-cd",
                new ShellApprovalInvocation(
                    $"cd {child.FullName} && cat result.txt | sed -n '1p'; touch marker.txt",
                    ApprovalDirectoryShape.None),
                grants,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/static-shell-failed-cd",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            Assert.False(decision.ApprovalContext?.IsMessy);
            Assert.Equal(["touch"], decision.ApprovalContext?.CandidateVerbs);
            Assert.Equal(project.FullName, Assert.Single(decision.ApprovalContext!.Candidates!).Directory);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "A sibling Bash directory needs its own folder grant.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Initial_grant_does_not_cover_the_sibling_success_scope()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-static-shell-sibling-");
        try
        {
            var initial = root.CreateSubdirectory("initial");
            var sibling = root.CreateSubdirectory("sibling");
            var grants = Approvals.Combine(
                Approvals.PersistentAnywhere("cd", "cat", "sed"),
                Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"));
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "static-shell-sibling-scope",
                new ShellApprovalInvocation(
                    $"cd {sibling.FullName} && cat result.txt | sed -n '1p'; touch marker.txt",
                    ApprovalDirectoryShape.None),
                grants,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    initial.FullName,
                    root.FullName,
                    "signalr/static-shell-sibling-scope",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            Assert.False(decision.ApprovalContext?.IsMessy);
            Assert.Equal(["touch"], decision.ApprovalContext?.CandidateVerbs);
            Assert.Equal(sibling.FullName, Assert.Single(decision.ApprovalContext!.Candidates!).Directory);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This case requires a POSIX symbolic link below the project root.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX symbolic link semantics.")]
    public async Task External_link_target_cannot_use_project_folder_grants()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-static-shell-link-");
        var external = Directory.CreateTempSubdirectory("netclaw-static-shell-external-");
        try
        {
            var link = Path.Combine(project.FullName, "linked");
            Directory.CreateSymbolicLink(link, external.FullName);
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "static-shell-link-escape",
                new ShellApprovalInvocation(
                    $"cd {link} && cat result.txt; touch marker.txt",
                    ApprovalDirectoryShape.None),
                Approvals.PersistentHere(ApprovalDirectoryShape.Project, "cd", "cat", "touch"),
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/static-shell-link-escape",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.NotEqual(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        }
        finally
        {
            project.Delete(recursive: true);
            external.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "These cases require POSIX Bash path and hard-deny policy.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "These cases require POSIX Bash semantics.")]
    public async Task Redirect_and_hard_deny_checks_precede_static_compound_grants()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-static-shell-deny-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            var commands = new[]
            {
                $"cd {child.FullName} && cat result.txt > /etc/passwd; ls .",
                $"cd {child.FullName} && rm -rf /; ls ."
            };
            foreach (var command in commands)
            {
                await using var harness = await ShellApprovalHarness.CreateAsync(
                    "static-shell-deny",
                    new ShellApprovalInvocation(command, ApprovalDirectoryShape.None),
                    Approvals.PersistentAnywhere("cd", "cat", "rm", "ls"),
                    fixture.ActorSystem,
                    TestContext.Current.CancellationToken,
                    scope: new ShellApprovalHarnessScope(
                        project.FullName,
                        project.FullName,
                        "signalr/static-shell-deny",
                        []),
                    deniedPaths: ["/etc/passwd"]);

                var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

                Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
            }
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "These cases require POSIX Bash directory and session semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "These cases require POSIX Bash semantics.")]
    public async Task Static_compound_keeps_session_and_audience_boundaries()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-static-shell-session-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            var invocation = new ShellApprovalInvocation(
                $"cd {child.FullName} && cat result.txt | sed -n '1p'; touch second.txt",
                ApprovalDirectoryShape.None,
                Interactive: false);
            var cases = new[]
            {
                (Name: "current", Grants: Approvals.Session("cd", "cat", "sed", "touch"), Expected: ToolAuthorizationOutcome.Allowed),
                (Name: "other", Grants: Approvals.SessionForOtherSession("cd", "cat", "sed", "touch"), Expected: ToolAuthorizationOutcome.RequiresApproval),
                (Name: "audience", Grants: Approvals.PersistentForOtherAudience("cd", "cat", "sed", "touch"), Expected: ToolAuthorizationOutcome.RequiresApproval)
            };
            foreach (var testCase in cases)
            {
                await using var harness = await ShellApprovalHarness.CreateAsync(
                    $"static-shell-{testCase.Name}",
                    invocation,
                    testCase.Grants,
                    fixture.ActorSystem,
                    TestContext.Current.CancellationToken,
                    scope: new ShellApprovalHarnessScope(
                        project.FullName,
                        project.FullName,
                        "signalr/static-shell-session",
                        []));

                var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

                Assert.True(
                    decision.Outcome == testCase.Expected,
                    $"case={testCase.Name}; outcome={decision.Outcome}; reason={decision.DenyReason}");
            }
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "The descendant glob can cross an unproved POSIX path segment.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash glob semantics.")]
    public async Task Deep_glob_keeps_exact_approval_in_a_static_compound()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-static-shell-glob-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "static-shell-deep-glob",
                new ShellApprovalInvocation(
                    $"cd {child.FullName} && cat */result.txt; ls .",
                    ApprovalDirectoryShape.Project),
                Approvals.PersistentAnywhere("cd", "cat", "ls"),
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/static-shell-deep-glob",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            Assert.True(decision.ApprovalContext?.IsMessy);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "The correction requires POSIX Bash directory semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Exact_child_directory_advice_stops_the_original_shell_process()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-shell-directory-advice-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            var marker = Path.Combine(child.FullName, "marker.txt");
            var testCase = new ShellApprovalCase(
                "exact-child-directory-advice",
                new ShellApprovalInvocation(
                    $"cd {child.FullName} && touch {marker}; cat */result.txt",
                    ApprovalDirectoryShape.None),
                Approvals.None,
                ExpectedApproval.Require([]));
            await using var harness = await ShellApprovalHarness.CreateAsync(
                testCase.Id,
                testCase.Invocation,
                testCase.Approvals,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/directory-advice",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            var correction = Assert.IsType<ToolCorrection.ShellWorkingDirectorySuggested>(decision.AgentCorrection);
            Assert.Equal(child.FullName, correction.Directory);
            Assert.Equal(ToolAuthorizationOutcome.RequiresAgentCorrection, decision.Outcome);

            await Assert.ThrowsAsync<ToolCorrectionRequiredException>(() =>
                harness.ExecuteAsync(TestContext.Current.CancellationToken));
            Assert.False(File.Exists(marker));

            await using var exactHarness = await ShellApprovalHarness.CreateAsync(
                "intentional-directory-behavior",
                testCase.Invocation with { WorkingDirectory = ApprovalDirectoryShape.Project },
                Approvals.None,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/directory-advice",
                    []));
            var exactDecision = await exactHarness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, exactDecision.Outcome);
            Assert.IsNotType<ToolCorrection.ShellWorkingDirectorySuggested>(exactDecision.AgentCorrection);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "The invalid target cases require POSIX path and symbolic link semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX path semantics.")]
    public async Task Unresolved_or_untrusted_directory_targets_receive_no_one_call_advice()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-shell-directory-boundary-");
        var external = Directory.CreateTempSubdirectory("netclaw-shell-directory-external-");
        try
        {
            var linked = Path.Combine(project.FullName, "linked");
            Directory.CreateSymbolicLink(linked, external.FullName);
            foreach (var target in new[] { "sub", linked, external.FullName })
            {
                var invocation = new ShellApprovalInvocation(
                    $"cd {target} && cat result.txt",
                    ApprovalDirectoryShape.None);
                await using var harness = await ShellApprovalHarness.CreateAsync(
                    "untrusted-directory-advice",
                    invocation,
                    Approvals.None,
                    fixture.ActorSystem,
                    TestContext.Current.CancellationToken,
                    scope: new ShellApprovalHarnessScope(
                        project.FullName,
                        project.FullName,
                        "signalr/directory-boundary",
                        []));

                var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

                Assert.IsNotType<ToolCorrection.ShellWorkingDirectorySuggested>(decision.AgentCorrection);
            }
        }
        finally
        {
            project.Delete(recursive: true);
            external.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "The requested directory behavior requires POSIX Bash semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This case requires POSIX Bash semantics.")]
    public async Task Simple_directory_behavior_uses_normal_approval_policy()
    {
        var project = Directory.CreateTempSubdirectory("netclaw-shell-directory-behavior-");
        try
        {
            var child = project.CreateSubdirectory("sub");
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "requested-directory-behavior",
                new ShellApprovalInvocation(
                    $"cd {child.FullName} && pwd",
                    ApprovalDirectoryShape.None),
                Approvals.None,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/directory-behavior",
                    []));

            var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

            Assert.IsNotType<ToolCorrection.ShellWorkingDirectorySuggested>(decision.AgentCorrection);
            Assert.NotEqual(ToolAuthorizationOutcome.RequiresAgentCorrection, decision.Outcome);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX glob, symlink, and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The project glob regression defines POSIX behavior.")]
    [InlineData("grep -rn \"Mode B\" docs/ *.md 2>/dev/null | head -20", true, "grep")]
    [InlineData("grep -rn \"Mode B\" docs/ *.md 2>/dev/null | head -20", false, "grep|head")]
    [InlineData("rm *.md", true, "rm")]
    [InlineData("rm *.md", false, "rm")]
    public async Task Project_glob_with_in_root_file_alias_remains_approval_gated(
        string command,
        bool interactive,
        string expectedCandidates)
    {
        var testCase = new ShellApprovalCase(
            "project-glob-with-in-root-alias-remains-approval-gated",
            new ShellApprovalInvocation(
                command,
                Interactive: interactive),
            Approvals.None,
            ExpectedApproval.Require(expectedCandidates.Split('|')));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("docs");
        harness.CreateProjectFileSymlink("CLAUDE.md", "AGENTS.md");

        var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal(expectedCandidates.Split('|'), observed.CandidateVerbs);
        Assert.False(observed.IsMessy);
        Assert.Equal(1, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public Task Noninteractive_safe_candidate_does_not_fill_a_partial_grant_gap()
        => AssertApprovalContract(new ShellApprovalCase(
            "noninteractive-partial-grant-keeps-safe-candidate-uncovered",
            new ShellApprovalInvocation("git push && git status", Interactive: false),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Require(
                ["git status"],
                approvalMatches: ["persistent:git push"])));

    [SlopwatchSuppress("SW001", "This regression requires POSIX symlink and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The symlink retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_rechecks_candidates_that_become_unsafe()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-safe-candidates",
            new ShellApprovalInvocation("cat leak/secret.txt && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["git push"], initial.ApprovalContext!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.ApprovalContext);
        harness.ReplaceProjectDirectoryWithExternalSymlink("leak");

        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, retry.Outcome);
        Assert.Equal(["cat", "git push"], retry.ApprovalContext!.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "This regression requires a POSIX shell cwd and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The project-scope correction defines Bash path behavior.")]
    public async Task Unavailable_registry_scope_preserves_ordinary_approval()
    {
        var testCase = new ShellApprovalCase(
            "reviewed-safe-external-cwd-suggests-project-scope",
            new ShellApprovalInvocation(
                "head -40 src/file.cs",
                ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["head"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);

        Assert.True(decision.NeedsApproval);
        Assert.Null(decision.AgentCorrection);
        Assert.NotNull(context.Cwd);
    }

    [SlopwatchSuppress("SW001", "This regression requires a POSIX shell cwd and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The project-scope correction defines Bash path behavior.")]
    public async Task Unsafe_external_cwd_does_not_expose_project_scope_correction()
    {
        var testCase = new ShellApprovalCase(
            "unsafe-external-cwd-keeps-normal-approval",
            new ShellApprovalInvocation(
                "git push",
                ApprovalDirectoryShape.External),
            Approvals.None,
            ExpectedApproval.Require(["git push"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        var context = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);

        Assert.Null(decision.AgentCorrection);
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX glob, symlink, and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The glob retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_does_not_cover_a_clean_command_that_becomes_messy()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-clean-to-messy",
            new ShellApprovalInvocation("cat artifacts/* && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("artifacts");

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["git push"], initial.ApprovalContext!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.ApprovalContext);
        harness.CreateProjectFileSymlinkToExternalFile("artifacts/leak");

        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, retry.Outcome);
        Assert.True(retry.ApprovalContext!.IsMessy);
        Assert.Empty(retry.ApprovalContext.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX symlink and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The symlink retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_rechecks_a_candidate_whose_stored_grant_stops_matching()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-stored-candidates",
            new ShellApprovalInvocation("git -C repo push && gh pr merge 123"),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "git push"),
            ExpectedApproval.Require(["gh pr merge"]));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("repo");

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["gh pr merge"], initial.ApprovalContext!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.ApprovalContext);
        harness.ReplaceProjectDirectoryWithExternalSymlink("repo");

        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, retry.Outcome);
        Assert.Equal(["git push", "gh pr merge"], retry.ApprovalContext!.CandidateVerbs);
    }

    [Fact]
    public Task Shell_approval_cases_match_review_table()
    {
        var settings = new VerifySettings();
        settings.DisableScrubbers();
        return Verifier.Verify(ShellApprovalCases.RenderReviewTable(), "md", settings);
    }
}

/// <summary>
/// Supplies source-level Slopwatch suppressions without a runtime package dependency.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class SlopwatchSuppressAttribute(string ruleId, string reason) : Attribute
{
    public string RuleId { get; } = ruleId;

    public string Reason { get; } = reason;
}
