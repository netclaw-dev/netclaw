// -----------------------------------------------------------------------
// <copyright file="ContainedGlobAliasAcceptanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ContainedGlobAliasAcceptanceTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_contained_alias_uses_the_existing_folder_grant(bool interactive, bool chain)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"), interactive);
        var target = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, ".target"));
        await File.WriteAllTextAsync(Path.Combine(target.FullName, "item.txt"), "original", Ct);
        if (chain)
        {
            Directory.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, ".hop"), target.FullName);
            Directory.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "alias"), ".hop");
        }
        else
        {
            Directory.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "alias"), target.FullName);
        }

        var decision = await harness.EvaluateShellAsync($"touch '{harness.ProjectDirectory}'/a*/*.txt", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, decision.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, decision.AllowReason);
        Assert.Null(decision.Prompt);
    }

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_protected_target_suffix_denies_even_with_a_global_grant(bool redirect)
    {
        using var directory = new DisposableTempDir();
        var project = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(project, ".vault", "data")).FullName;
        var protectedFile = Path.Combine(target, "credential.txt");
        await File.WriteAllTextAsync(protectedFile, "private", Ct);
        Directory.CreateSymbolicLink(Path.Combine(project, "alias"), target);
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentAnywhere("cat", "date"), true, project, [protectedFile]);
        var command = redirect ? $"date > '{project}'/a*/*.txt" : $"cat '{project}'/a*/*.txt";

        var decision = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal(0, decision.ApprovalChecks);
        Assert.Null(decision.Prompt);
        Assert.Equal("private", await File.ReadAllTextAsync(protectedFile, Ct));
    }

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_sibling_does_not_erase_a_protected_target(bool brokenFirst)
    {
        using var directory = new DisposableTempDir();
        var project = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(project, ".vault")).FullName;
        var protectedFile = Path.Combine(target, "credential.txt");
        await File.WriteAllTextAsync(protectedFile, "private", Ct);
        if (brokenFirst)
            Directory.CreateSymbolicLink(Path.Combine(project, "alias-broken"), Path.Combine(project, "absent"));
        Directory.CreateSymbolicLink(Path.Combine(project, "alias-private"), target);
        if (!brokenFirst)
            Directory.CreateSymbolicLink(Path.Combine(project, "alias-broken"), Path.Combine(project, "absent"));
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentAnywhere("cat"), true, project, [protectedFile]);

        var decision = await harness.EvaluateShellAsync($"cat '{project}'/alias-*/*.txt", Ct);

        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal(0, decision.ApprovalChecks);
        Assert.Null(decision.Prompt);
    }

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData("outside", true)]
    [InlineData("outside", false)]
    [InlineData("broken", true)]
    [InlineData("cycle", true)]
    public async Task An_unresolved_alias_does_not_reuse_a_global_grant(string shape, bool interactive)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("touch"), interactive);
        var alias = Path.Combine(harness.ProjectDirectory, "alias");
        var target = shape switch
        {
            "outside" => Path.GetDirectoryName(harness.ProjectDirectory)!,
            "broken" => Path.Combine(harness.ProjectDirectory, "absent"),
            "cycle" => alias,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        Directory.CreateSymbolicLink(alias, target);

        var decision = await harness.EvaluateShellAsync($"touch '{harness.ProjectDirectory}'/a*/*.txt", Ct);

        Assert.Equal(interactive ? ApprovalOutcome.RequiresApproval : ApprovalOutcome.Denied, decision.Outcome);
        if (interactive)
        {
            Assert.NotNull(decision.Prompt);
            Assert.Contains(ObservedOptionKeys.ApproveOnce, decision.Prompt.OptionKeys);
            Assert.DoesNotContain(ObservedOptionKeys.ApproveEverywhere, decision.Prompt.OptionKeys);
            Assert.DoesNotContain(ObservedOptionKeys.ApproveAlways, decision.Prompt.OptionKeys);
        }
    }

    [SlopwatchSuppress("SW001", "The glob facts require the native POSIX path model.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The glob facts require POSIX paths.")]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task The_directory_budget_has_an_explicit_authority_boundary(int directories, bool allowed)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"), true);
        for (var index = 0; index < directories; index++)
            Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, $"entry-{index:D4}"));

        var decision = await harness.EvaluateShellAsync($"touch '{harness.ProjectDirectory}'/*/item.txt", Ct);

        Assert.Equal(allowed ? ApprovalOutcome.Allowed : ApprovalOutcome.RequiresApproval, decision.Outcome);
    }

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Approval)]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Auto)]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Approval)]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Auto)]
    public async Task Nonpersonal_audiences_cannot_use_aliases_to_bypass_the_shell_boundary(
        TrustAudience audience, ToolApprovalMode mode)
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "contained-alias-audience",
            new ShellApprovalInvocation("true", Audience: audience, Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            policy: new ShellApprovalHarnessPolicy
            {
                ConfigureTools = tools =>
                {
                    var profile = audience == TrustAudience.Team
                        ? tools.AudienceProfiles.Team
                        : tools.AudienceProfiles.Public;
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
        var target = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, ".target")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "leaf"));
        Directory.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "alias"), target);

        var decision = await harness.EvaluateShellAsync($"ls -d '{harness.ProjectDirectory}'/a*/*/", Ct);

        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_requires_personal_context", decision.DenyReason);
        Assert.Equal(0, decision.ApprovalChecks);
    }

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auto_consent_does_not_replace_target_protection(bool protectTarget)
    {
        using var directory = new DisposableTempDir();
        var project = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(project, ".vault")).FullName;
        var file = Path.Combine(target, "item.txt");
        await File.WriteAllTextAsync(file, "original", Ct);
        Directory.CreateSymbolicLink(Path.Combine(project, "alias"), target);
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "contained-alias-auto",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct,
            scope: new ShellApprovalHarnessScope(project, project, "signalr/contained-alias-auto", []),
            deniedPaths: protectTarget ? [file] : [],
            shellApprovalMode: ToolApprovalMode.Auto);

        var decision = await harness.EvaluateShellAsync($"cat '{project}'/a*/*.txt", Ct);

        Assert.Equal(protectTarget ? ApprovalOutcome.Denied : ApprovalOutcome.Allowed, decision.Outcome);
        Assert.Equal(0, decision.ApprovalChecks);
        Assert.Null(decision.Prompt);
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(
        ApprovalState approvals,
        bool interactive,
        string? project = null,
        IReadOnlyList<string>? deniedPaths = null)
        => ShellApprovalHarness.CreateAsync(
            "contained-alias-independent",
            new ShellApprovalInvocation("true", Interactive: interactive, Host: ShellApprovalHost.Bash52),
            approvals,
            fixture.ActorSystem,
            Ct,
            scope: project is null ? null : new ShellApprovalHarnessScope(
                project, project, "signalr/contained-alias-independent", []),
            deniedPaths: deniedPaths);
}
