// -----------------------------------------------------------------------
// <copyright file="ShellApprovalDispositionMatrixTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Configuration;
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

    // An interactive reviewed phrase covers each path that the audience may
    // read. These cases need the project and session roots only, so the
    // profile reads no other root.
    private Task<ShellApprovalHarness> CreateWithConfinedReadsAsync(ShellApprovalCase testCase)
        => ShellApprovalHarness.CreateAsync(
            testCase.Id,
            testCase.Invocation,
            testCase.Approvals,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            policy: new ShellApprovalHarnessPolicy
            {
                ConfigureTools = config =>
                {
                    config.AudienceProfiles.GlobalReadRoots = [];
                    config.AudienceProfiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
                    {
                        Mode = ToolFilesystemMode.Roots,
                        Roots = []
                    };
                }
            });

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
        // A prompt that names no command shows its full command text, or the
        // statement patterns when it has them (PowerShell).
        IReadOnlyList<string> expectedCandidates = testCase.Expected.Candidates is [ExpectedApproval.FullCommandText]
            ? observed.Prompt is { Patterns.Count: > 0 }
                ? []
                : [observed.Prompt?.DisplayText ?? ExpectedApproval.FullCommandText]
            : testCase.Expected.Candidates;
        Assert.Equal(expectedCandidates, observed.Prompt?.CandidateVerbs ?? []);
        Assert.Equal(testCase.Expected.IsMessy, observed.Prompt?.IsMessy);
        Assert.Equal(testCase.Expected.ApprovalChecks, observed.ApprovalChecks);
        Assert.Equal(testCase.Expected.ApprovalMatches, observed.ApprovalMatches);
    }

    // The rows come from the bundled catalog, so a new catalog entry gets this
    // proof with no test edit. The bare phrase runs in the project through the
    // production approval path and must need no prompt.
    public static TheoryData<string> BundledBashPhrases
        => new(SafeVerbLoader.Load(isWindows: false).Verbs);

    public static TheoryData<string> BundledPowerShellPhrases
        => new(SafeVerbLoader.Load(isWindows: true).Verbs);

    [SlopwatchSuppress("SW001", "The Bash catalog rows require a POSIX filesystem.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Bash catalog rows require POSIX filesystem semantics.")]
    [MemberData(nameof(BundledBashPhrases))]
    public Task Bundled_bash_catalog_phrase_needs_no_prompt_in_the_project(string phrase)
        => AssertApprovalContract(new ShellApprovalCase(
            $"bundled-bash-catalog:{phrase}",
            new ShellApprovalInvocation(phrase),
            Approvals.None,
            ExpectedApproval.Allow(ApprovalAllowReason.ReviewedSafePolicy)));

    [Theory]
    [MemberData(nameof(BundledPowerShellPhrases))]
    public Task Bundled_power_shell_catalog_phrase_needs_no_prompt_in_the_project(string phrase)
        => AssertApprovalContract(new ShellApprovalCase(
            $"bundled-powershell-catalog:{phrase}",
            new ShellApprovalInvocation(phrase, Host: ShellApprovalHost.PowerShell7),
            Approvals.None,
            ExpectedApproval.Allow(ApprovalAllowReason.ReviewedSafePolicy)));

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
            ExpectedApproval.Allow(ApprovalAllowReason.ReviewedSafePolicy)));
    }

    [Fact]
    public Task Noninteractive_reviewed_safe_candidate_uses_reviewed_policy()
        => AssertApprovalContract(new ShellApprovalCase(
            "noninteractive-reviewed-safe-allows",
            OperatingSystem.IsWindows()
                ? new ShellApprovalInvocation("Get-Date", Host: ShellApprovalHost.PowerShell7, Interactive: false)
                : new ShellApprovalInvocation("git status", Interactive: false),
            Approvals.None,
            ExpectedApproval.Allow(ApprovalAllowReason.ReviewedSafePolicy)));

    [Fact]
    public Task Noninteractive_candidate_can_use_an_explicit_persistent_grant()
        => AssertApprovalContract(new ShellApprovalCase(
            "noninteractive-reviewed-safe-with-grant-allows",
            new ShellApprovalInvocation("git status", Interactive: false),
            Approvals.PersistentAnywhere("git status"),
            ExpectedApproval.Allow(
                ApprovalAllowReason.StoredApproval,
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

        var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        var approval = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);
        Assert.False(approval.IsMessy);
        Assert.Equal(["git push"], approval.CandidateVerbs);
        Assert.Contains(
            approval.OptionKeys,
            key => key == ObservedOptionKeys.ApproveSession);
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
            ExpectedApproval.RequireFullText());
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("sub");

        var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        // The relative cd has no proved target, so each later command is one
        // exact candidate. The grant covers only the cd.
        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Equal(["cat result.txt", "sed -n '1p'", "ls ."], decision.Prompt!.CandidateVerbs);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], decision.Prompt.OptionKeys);
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalOutcome.Allowed, decision.Outcome);
            Assert.Equal(ApprovalAllowReason.StoredApproval, decision.AllowReason);

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
            var missingDecision = await missingStage.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalOutcome.RequiresApproval, missingDecision.Outcome);
            Assert.False(missingDecision.Prompt?.IsMessy);
            Assert.Equal(["sed"], missingDecision.Prompt?.CandidateVerbs);
            Assert.Contains(
                missingDecision.Prompt!.OptionKeys,
                key => key == ObservedOptionKeys.ApproveSession);
            missingStage.SeedOneTimeApproval(missingDecision.Prompt!);
            var retryDecision = await missingStage.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalOutcome.Allowed, retryDecision.Outcome);
            Assert.Equal(ApprovalAllowReason.OneTimeApproval, retryDecision.AllowReason);
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
            Assert.False(decision.Prompt?.IsMessy);
            Assert.Equal(["touch"], decision.Prompt?.CandidateVerbs);
            Assert.Equal(project.FullName, Assert.Single(decision.Prompt!.CandidateDirectories!));
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
            Assert.False(decision.Prompt?.IsMessy);
            Assert.Equal(["touch"], decision.Prompt?.CandidateVerbs);
            Assert.Equal(sibling.FullName, Assert.Single(decision.Prompt!.CandidateDirectories!));
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            Assert.NotEqual(ApprovalOutcome.Allowed, decision.Outcome);
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

                var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

                Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
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
                (Name: "current", Grants: Approvals.Session("cd", "cat", "sed", "touch"), Expected: ApprovalOutcome.Allowed),
                // An unattended call that would prompt is denied (D2).
                (Name: "other", Grants: Approvals.SessionForOtherSession("cd", "cat", "sed", "touch"), Expected: ApprovalOutcome.Denied),
                (Name: "audience", Grants: Approvals.PersistentForOtherAudience("cd", "cat", "sed", "touch"), Expected: ApprovalOutcome.Denied)
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

                var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            // The deep glob and the command after the list are exact candidates.
            Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
            Assert.Equal(["cat */result.txt", "ls ."], decision.Prompt!.CandidateVerbs);
            Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], decision.Prompt.OptionKeys);
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
                ExpectedApproval.Correct());
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalCorrection.ShellWorkingDirectory, decision.AgentCorrection);
            Assert.Equal(child.FullName, decision.AgentCorrectionTarget);
            Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, decision.Outcome);

            await Assert.ThrowsAsync<ShellApprovalCorrectionRequiredException>(() =>
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
            var exactDecision = await exactHarness.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalOutcome.RequiresApproval, exactDecision.Outcome);
            Assert.NotEqual(ApprovalCorrection.ShellWorkingDirectory, exactDecision.AgentCorrection);
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

                var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

                Assert.NotEqual(ApprovalCorrection.ShellWorkingDirectory, decision.AgentCorrection);
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

            var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            Assert.NotEqual(ApprovalCorrection.ShellWorkingDirectory, decision.AgentCorrection);
            Assert.NotEqual(ApprovalOutcome.RequiresAgentCorrection, decision.Outcome);
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX glob, symlink, and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The project glob regression defines POSIX behavior.")]
    // A bare glob gets a rewrite correction (#2306), so the cases use the path glob ./*.md.
    // The read is a reviewed diagnostic, attended or not (D2); the next test covers it.
    [InlineData("rm ./*.md", true, "rm")]
    [InlineData("rm ./*.md", false, "rm")]
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

        Assert.Equal(1, observed.ApprovalChecks);
        if (!interactive)
        {
            // Nobody can answer the prompt in an unattended run (D2).
            Assert.Equal(ApprovalOutcome.Denied, observed.Outcome);
            Assert.Equal(ToolAuthorizer.UnattendedApprovalRequired, observed.DenyReason);
            return;
        }

        Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
        Assert.Equal(expectedCandidates.Split('|'), observed.Prompt?.CandidateVerbs);
        Assert.False(observed.Prompt?.IsMessy);
    }

    // The in-root alias only keeps the analysis complete. The reviewed catalog
    // then decides: an interactive read of project files runs with no prompt,
    // and a redirect to /dev/null writes no file. A real output file still prompts.
    [SlopwatchSuppress("SW001", "This regression requires POSIX glob, symlink, and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The project glob regression defines POSIX behavior.")]
    [InlineData("grep -rn \"Mode B\" docs/ ./*.md 2>/dev/null | head -20", null)]
    [InlineData("grep -rn \"Mode B\" docs/ ./*.md | head -20", null)]
    [InlineData("grep -rn \"Mode B\" docs/ ./*.md > hits.txt", "grep")]
    public async Task Project_glob_with_in_root_file_alias_reads_with_reviewed_catalog(
        string command,
        string? expectedCandidates)
    {
        var testCase = new ShellApprovalCase(
            "project-glob-with-in-root-alias-reads-with-reviewed-catalog",
            new ShellApprovalInvocation(command),
            Approvals.None,
            expectedCandidates is null
                ? ExpectedApproval.Allow(ApprovalAllowReason.ReviewedSafePolicy)
                : ExpectedApproval.Require(expectedCandidates.Split('|')));
        await using var harness = await ShellApprovalHarness.CreateAsync(
            testCase,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken);
        harness.CreateProjectDirectory("docs");
        harness.CreateProjectFileSymlink("CLAUDE.md", "AGENTS.md");

        var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        if (expectedCandidates is null)
        {
            Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
            Assert.Equal(ApprovalAllowReason.ReviewedSafePolicy, observed.AllowReason);
        }
        else
        {
            Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
            Assert.Equal(expectedCandidates.Split('|'), observed.Prompt?.CandidateVerbs);
        }
    }

    // A reviewed cd into a project folder needs no prompt. It covers only the
    // directory change: each later command keeps its own check. {src} is the
    // absolute path of a project folder; the parser does not resolve a relative cd.
    [SlopwatchSuppress("SW001", "This regression requires POSIX directory and Bash authorization behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The cd regression defines Bash behavior.")]
    [InlineData("cd {src} && git status", null)]
    [InlineData("cd {src} && ls -la; pwd", null)]
    [InlineData("cd {src} && rm -rf build", "rm")]
    [InlineData("cd {src} && git push", "git push")]
    // The cd leaves the project. An interactive run may read there, so it needs no prompt.
    [InlineData("cd {src}/../.. && ls", null)]
    public async Task Reviewed_cd_into_project_folder_keeps_later_checks(
        string command,
        string? expectedCandidates)
    {
        var project = Directory.CreateTempSubdirectory("netclaw-reviewed-cd-");
        try
        {
            var source = project.CreateSubdirectory("src");
            await using var harness = await ShellApprovalHarness.CreateAsync(
                "reviewed-cd-into-project-folder",
                new ShellApprovalInvocation(
                    command.Replace("{src}", source.FullName, StringComparison.Ordinal),
                    ApprovalDirectoryShape.None),
                Approvals.None,
                fixture.ActorSystem,
                TestContext.Current.CancellationToken,
                scope: new ShellApprovalHarnessScope(
                    project.FullName,
                    project.FullName,
                    "signalr/reviewed-cd",
                    []));

            var observed = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

            if (expectedCandidates is null)
            {
                Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
                Assert.Equal(ApprovalAllowReason.ReviewedSafePolicy, observed.AllowReason);
            }
            else
            {
                Assert.Equal(ApprovalOutcome.RequiresApproval, observed.Outcome);
                Assert.Equal(expectedCandidates.Split('|'), observed.Prompt?.CandidateVerbs);
            }
        }
        finally
        {
            project.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "The git status phrase is in the Bash reviewed catalog only.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The git status phrase is in the Bash reviewed catalog only.")]
    public Task Noninteractive_safe_candidate_fills_the_gap_of_a_partial_grant()
        => AssertApprovalContract(new ShellApprovalCase(
            "noninteractive-partial-grant-and-safe-candidate-allow",
            new ShellApprovalInvocation("git push && git status", Interactive: false),
            Approvals.PersistentAnywhere("git push"),
            ExpectedApproval.Allow(ApprovalAllowReason.StoredApproval, 1, "persistent:git push")));

    [SlopwatchSuppress("SW001", "This regression requires POSIX symlink and Bash authorization behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The symlink retry regression defines Bash authorization behavior.")]
    public async Task One_time_retry_rechecks_candidates_that_become_unsafe()
    {
        var testCase = new ShellApprovalCase(
            "one-time-retry-rechecks-safe-candidates",
            new ShellApprovalInvocation("cat leak/secret.txt && git push"),
            Approvals.None,
            ExpectedApproval.Require(["git push"]));
        await using var harness = await CreateWithConfinedReadsAsync(testCase);

        var initial = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["git push"], initial.Prompt!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.Prompt);
        harness.ReplaceProjectDirectoryWithExternalSymlink("leak");

        var retry = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.RequiresApproval, retry.Outcome);
        Assert.Equal(["cat", "git push"], retry.Prompt!.CandidateVerbs);
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
        await using var harness = await CreateWithConfinedReadsAsync(testCase);

        var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
        var context = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);

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

        var decision = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
        var context = Assert.IsType<ApprovalPromptObservation>(decision.Prompt);

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

        var initial = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["git push"], initial.Prompt!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.Prompt);
        harness.CreateProjectFileSymlinkToExternalFile("artifacts/leak");

        var retry = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        // The glob is now exact. The "Once" answer for git push does not cover it.
        Assert.Equal(ApprovalOutcome.RequiresApproval, retry.Outcome);
        Assert.Contains("cat artifacts/*", retry.Prompt!.CandidateVerbs);
        Assert.Equal([ObservedOptionKeys.ApproveOnce, ObservedOptionKeys.Deny], retry.Prompt.OptionKeys);
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

        var initial = await harness.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["gh pr merge"], initial.Prompt!.CandidateVerbs);
        harness.SeedOneTimeApproval(initial.Prompt);
        harness.ReplaceProjectDirectoryWithExternalSymlink("repo");

        var retry = await harness.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.RequiresApproval, retry.Outcome);
        Assert.Equal(["git push", "gh pr merge"], retry.Prompt!.CandidateVerbs);
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
