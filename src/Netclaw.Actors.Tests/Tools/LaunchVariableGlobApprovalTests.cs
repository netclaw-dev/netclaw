// -----------------------------------------------------------------------
// <copyright file="LaunchVariableGlobApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class LaunchVariableGlobApprovalTests(ShellApprovalMatrixFixture fixture)
{
    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    [InlineData("\"$HOME\"")]
    [InlineData("$HOME")]
    [InlineData("${HOME}")]
    public async Task Proved_home_glob_uses_the_reviewed_diagnostic_policy(string home)
    {
        await using var harness = await CreateAsync(Approvals.None);
        var path = UniqueHomePath(harness, home);

        var literal = Path.Combine(new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52)
            .CreateEnvironment().HomeDirectory!, Path.GetFileName(path));
        var twin = await harness.EvaluateShellAsync($"ls -d '{literal}'/*/ | head -60",
            TestContext.Current.CancellationToken);
        var result = await harness.EvaluateShellAsync($"ls -d {path}/*/ | head -60",
            TestContext.Current.CancellationToken, workingDirectory: harness.ProjectDirectory);

        Assert.Equal(ApprovalOutcome.Allowed, result.Outcome);
        Assert.Equal(ApprovalAllowReason.ReviewedSafePolicy, result.AllowReason);
        Assert.Equal(twin.Outcome, result.Outcome);
        Assert.Equal(twin.AllowReason, result.AllowReason);
    }

    [SlopwatchSuppress("SW001", "The launch facts and folder grants require POSIX Bash paths.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts and folder grants require POSIX Bash paths.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Home_glob_outside_a_folder_grant_needs_consent(bool anywhere)
    {
        var grants = anywhere
            ? Approvals.PersistentAnywhere("rm")
            : Approvals.PersistentHere(ApprovalDirectoryShape.Project, "rm");
        await using var harness = await CreateAsync(grants);
        var path = UniqueHomePath(harness, "\"$HOME\"");

        var result = await harness.EvaluateShellAsync($"rm {path}/*.tmp",
            TestContext.Current.CancellationToken, workingDirectory: harness.ProjectDirectory);

        Assert.Equal(anywhere ? ApprovalOutcome.Allowed : ApprovalOutcome.RequiresApproval, result.Outcome);
        if (!anywhere)
            Assert.Equal(["rm"], result.Prompt!.CandidateVerbs);
    }

    [Theory]
    [InlineData("cat ${HOME}/.netclaw/*/secrets.json")]
    [InlineData("sudo ls \"$HOME\"/*")]
    public async Task A_global_grant_preserves_protected_path_and_hard_deny_rules(string command)
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("cat", "ls", "sudo"));

        var result = await harness.EvaluateShellAsync(command,
            TestContext.Current.CancellationToken, workingDirectory: harness.ProjectDirectory);

        Assert.Equal(ApprovalOutcome.Denied, result.Outcome);
        Assert.Equal(0, result.ApprovalChecks);
    }

    [SlopwatchSuppress("SW001", "The protected key path requires the POSIX Bash HOME fact.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The protected key path requires the POSIX Bash HOME fact.")]
    public async Task A_global_grant_preserves_the_protected_home_key_boundary()
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("cat", "ls", "sudo"));

        var result = await harness.EvaluateShellAsync("cat \"$HOME\"/.netclaw/k*/*.xml",
            TestContext.Current.CancellationToken, workingDirectory: harness.ProjectDirectory);

        Assert.Equal(ApprovalOutcome.Denied, result.Outcome);
        Assert.Equal(0, result.ApprovalChecks);
    }

    [Fact]
    public async Task An_unknown_glob_value_requires_correction_despite_a_global_grant()
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("rm"));

        var result = await harness.EvaluateShellAsync("rm \"$SST_UNSUPPLIED\"/proof/*",
            TestContext.Current.CancellationToken, workingDirectory: harness.ProjectDirectory);

        Assert.Equal(ApprovalOutcome.RequiresAgentCorrection, result.Outcome);
        Assert.Equal(ApprovalCorrection.ShellWordQuote, result.AgentCorrection);
    }

    [SlopwatchSuppress("SW001", "The producer needs proved HOME facts from the POSIX Bash host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The producer needs POSIX Bash launch facts.")]
    public async Task A_shell_consumer_keeps_its_own_consent_requirement()
    {
        await using var harness = await CreateAsync(Approvals.None);
        var path = UniqueHomePath(harness, "\"$HOME\"");

        var result = await harness.EvaluateShellAsync($"ls -d {path}/*/ | sh",
            TestContext.Current.CancellationToken, workingDirectory: harness.ProjectDirectory);

        Assert.Equal(ApprovalOutcome.RequiresApproval, result.Outcome);
        Assert.Equal(["sh"], result.Prompt!.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    [InlineData("$HOME")]
    [InlineData("${HOME}")]
    [InlineData("\"$HOME\"")]
    public async Task A_global_grant_covers_a_proved_home_operand_without_a_split_correction(string home)
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("rm"));
        var result = await harness.EvaluateShellAsync($"rm {UniqueHomePath(harness, home)}/*/",
            TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.Allowed, result.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, result.AllowReason);
        Assert.Null(result.AgentCorrection);
    }

    [SlopwatchSuppress("SW001", "The fixture requires POSIX paths and the git CLI.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX paths and the git CLI.")]
    public async Task A_repository_grant_does_not_cover_an_external_home_operand()
    {
        using var directory = new DisposableTempDir();
        await ApprovalTestGit.CreateRepositoryAsync(directory.Path);
        await using var harness = await ShellApprovalHarness.CreateAsync("launch-variable-repository",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.PersistentRepository("rm"), fixture.ActorSystem, TestContext.Current.CancellationToken,
            scope: new ShellApprovalHarnessScope(directory.Path, directory.Path, "signalr/variable-repository", [])
            {
                RepositoryGrantWorktree = directory.Path
            });
        var result = await harness.EvaluateShellAsync($"rm {UniqueHomePath(harness, "$HOME")}/*/",
            TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.RequiresApproval, result.Outcome);
        Assert.Equal(["rm"], result.Prompt!.CandidateVerbs);
    }

    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    [InlineData("$TMPDIR")]
    [InlineData("${TMPDIR}")]
    [InlineData("\"$TMPDIR\"")]
    public async Task A_temporary_path_operand_matches_its_literal_twin(string temporary)
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("rm"));
        var variable = await harness.EvaluateShellAsync($"rm {temporary}/proof/*/",
            TestContext.Current.CancellationToken);
        var literal = await harness.EvaluateShellAsync($"rm '{harness.ManagedTemporaryDirectory}'/proof/*/",
            TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.Allowed, variable.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, variable.AllowReason);
        Assert.Equal(literal.Outcome, variable.Outcome);
        Assert.Equal(literal.AllowReason, variable.AllowReason);
    }

    [Theory]
    [InlineData("HOME=/outside; rm \"$HOME\"/proof/*/")]
    [InlineData("r='x push'; git --c=$r log")]
    [InlineData("git ${SST_UNSUPPLIED}*")]
    [InlineData("git p?sh")]
    [InlineData("r='push'; git $r")]
    [InlineData("rm $SST_UNSUPPLIED/proof/*/")]
    public async Task Unproved_word_shapes_do_not_consume_a_global_grant(string command)
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("rm", "git"));
        var result = await harness.EvaluateShellAsync(command, TestContext.Current.CancellationToken);

        Assert.Contains(result.Outcome, new[]
        {
            ApprovalOutcome.RequiresAgentCorrection, ApprovalOutcome.RequiresApproval, ApprovalOutcome.Denied
        });
        Assert.Null(result.AllowReason);
    }

    [Theory]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Approval)]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Auto)]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Approval)]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Auto)]
    public async Task A_launch_variable_keeps_the_personal_shell_boundary(TrustAudience audience, ToolApprovalMode mode)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync("launch-variable-audience",
            new ShellApprovalInvocation("true", Audience: audience, Host: ShellApprovalHost.Bash52),
            Approvals.None, fixture.ActorSystem, TestContext.Current.CancellationToken,
            policy: new ShellApprovalHarnessPolicy
            {
                ConfigureTools = tools =>
                {
                    var profile = audience == TrustAudience.Team ? tools.AudienceProfiles.Team : tools.AudienceProfiles.Public;
                    profile.ToolsMode = ToolProfileMode.All;
                    profile.ReadFiles.Mode = ToolFilesystemMode.All;
                    profile.WriteFiles.Mode = ToolFilesystemMode.All;
                    profile.ApprovalPolicy = new ToolApprovalConfig
                    {
                        ToolOverrides = new Dictionary<string, ToolApprovalMode>
                        {
                            [ToolAudienceProfileToolCatalog.ShellExecute] = mode
                        }
                    };
                }
            });
        var result = await harness.EvaluateShellAsync("ls -d $HOME/proof/*/", TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.Denied, result.Outcome);
        Assert.Equal("shell_requires_personal_context", result.DenyReason);
        Assert.Equal(0, result.ApprovalChecks);
    }

    [Fact]
    public Task Auto_consent_allows_an_ordinary_launch_variable_operand()
        => AssertAutoConsentAsync(protect: false);

    [SlopwatchSuppress("SW001", "Protected HOME paths require the POSIX Bash launch facts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Protected HOME paths require POSIX Bash launch facts.")]
    public Task Auto_consent_keeps_protected_launch_variable_paths()
        => AssertAutoConsentAsync(protect: true);

    private async Task AssertAutoConsentAsync(bool protect)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync("launch-variable-auto",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52), Approvals.None,
            fixture.ActorSystem, TestContext.Current.CancellationToken, shellApprovalMode: ToolApprovalMode.Auto);
        var command = protect
            ? "cat $HOME/.netclaw/k*/*.xml"
            : $"rm {UniqueHomePath(harness, "$HOME")}/*/";
        var result = await harness.EvaluateShellAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(protect ? ApprovalOutcome.Denied : ApprovalOutcome.Allowed, result.Outcome);
        if (!protect)
            Assert.Equal(ApprovalAllowReason.PolicyAuto, result.AllowReason);
        Assert.Equal(0, result.ApprovalChecks);
    }

    [SlopwatchSuppress("SW001", "The scalar twin requires a POSIX Bash working directory.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The scalar twin requires a POSIX Bash working directory.")]
    public async Task An_exact_scalar_option_matches_its_literal_twin()
    {
        await using var harness = await CreateAsync(Approvals.PersistentAnywhere("git"));
        var variable = await harness.EvaluateShellAsync("r='--help'; git $r", TestContext.Current.CancellationToken);
        var literal = await harness.EvaluateShellAsync("git --help", TestContext.Current.CancellationToken);

        Assert.Equal(ApprovalOutcome.Allowed, literal.Outcome);
        Assert.Equal(literal.Outcome, variable.Outcome);
        Assert.Equal(literal.AllowReason, variable.AllowReason);
    }

    public static bool IsPosix => TestPlatform.IsPosix;

    [SlopwatchSuppress("SW001", "The fixture requires POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_launch_variable_preserves_alias_source_and_target_scopes(bool outside, bool protect)
    {
        using var directory = new DisposableTempDir();
        var project = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(project, "tmp", "parent", "proof")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(outside ? directory.Path : source, ".target")).FullName;
        var file = Path.Combine(target, "item.tmp");
        await File.WriteAllTextAsync(file, "original", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Combine(source, "alias"), target);
        await using var harness = await ShellApprovalHarness.CreateAsync("launch-variable-alias",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"), fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            scope: new ShellApprovalHarnessScope(project, project, "signalr/variable-alias", [])
            {
                TemporaryLayout = ManagedTemporaryLayout.InsideSessionStorage.Instance
            }, deniedPaths: protect ? [file] : []);

        var variable = await harness.EvaluateShellAsync("touch $TMPDIR/proof/a*/*.tmp",
            TestContext.Current.CancellationToken);
        var literal = await harness.EvaluateShellAsync($"touch '{harness.ManagedTemporaryDirectory}'/proof/a*/*.tmp",
            TestContext.Current.CancellationToken);

        Assert.Equal(protect ? ApprovalOutcome.Denied : outside ? ApprovalOutcome.RequiresApproval : ApprovalOutcome.Allowed,
            variable.Outcome);
        Assert.Equal(literal.Outcome, variable.Outcome);
        Assert.Equal(literal.AllowReason, variable.AllowReason);
        Assert.Equal("original", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
    }

    [SlopwatchSuppress("SW001", "The fixture requires POSIX file aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX file aliases.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_private_alias_keeps_both_authority_candidates(bool anywhere)
    {
        await using var harness = await CreateAsync(anywhere
            ? Approvals.PersistentAnywhere("touch")
            : Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"));
        var targetDirectory = Directory.CreateDirectory(Path.Combine(harness.SessionDirectory, "target")).FullName;
        var target = Path.Combine(targetDirectory, "item.tmp");
        await File.WriteAllTextAsync(target, "original", TestContext.Current.CancellationToken);
        var alias = Path.Combine(harness.ProjectDirectory, "alias.tmp");
        File.CreateSymbolicLink(alias, target);
        var command = $"touch '{alias}'";
        var matcher = new ShellApprovalMatcher(new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52)
            .CreateEnvironment());
        var candidates = matcher.ExtractCandidates(new ToolName(ShellTool.ToolName),
            ToolInput.Create("Command", command, "WorkingDirectory", harness.ProjectDirectory));

        var result = await harness.EvaluateShellAsync(command, TestContext.Current.CancellationToken);

        Assert.Contains(harness.ProjectDirectory, candidates.Select(candidate => candidate.Directory));
        Assert.Contains(targetDirectory, candidates.Select(candidate => candidate.Directory));
        Assert.Equal(anywhere ? ApprovalOutcome.Allowed : ApprovalOutcome.RequiresApproval, result.Outcome);
        if (anywhere)
            Assert.Equal(ApprovalAllowReason.StoredApproval, result.AllowReason);
        else
        {
            Assert.Contains(targetDirectory, result.Prompt!.CandidateDirectories ?? []);
            Assert.Contains(result.TraceRows, row => row.Contains("|OutsideDirectory|", StringComparison.Ordinal));
        }
        Assert.Equal("original", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    private Task<ShellApprovalHarness> CreateAsync(ApprovalState grants)
        => ShellApprovalHarness.CreateAsync("launch-variable-glob",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            grants, fixture.ActorSystem, TestContext.Current.CancellationToken);

    private static string UniqueHomePath(ShellApprovalHarness harness, string home)
        => $"{home}/sst-proof-{Path.GetFileName(Path.GetDirectoryName(harness.ProjectDirectory))}";
}
