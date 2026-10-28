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

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX file aliases.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX file aliases.")]
    [InlineData("local", true)]
    [InlineData("roundtrip", true)]
    [InlineData("sibling", false)]
    [InlineData("outside", false)]
    [InlineData("sibling-directory", false)]
    [InlineData("nonmatching-leaf", false)]
    [InlineData("nonmatching-intermediate", false)]
    public async Task A_leaf_alias_retains_its_immediate_directory_boundary(string shape, bool allowed)
    {
        await using var harness = await CreateHarnessAsync(
            Approvals.PersistentHere(ApprovalDirectoryShape.Project, "touch"), true);
        var source = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, "a")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, "b")).FullName;
        var targetDirectory = shape switch
        {
            "local" or "roundtrip" => source,
            "sibling" or "sibling-directory" or "nonmatching-leaf" or "nonmatching-intermediate" => sibling,
            "outside" => harness.SessionDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var target = Path.Combine(targetDirectory, "item.txt");
        await File.WriteAllTextAsync(target, "original", Ct);
        var pattern = shape switch
        {
            "sibling-directory" => "*/*/",
            "roundtrip" => "a*/*.txt",
            "nonmatching-leaf" => "*/wanted*.txt",
            "nonmatching-intermediate" => "*/x*/*.txt",
            _ => "*/*.txt"
        };
        if (shape == "roundtrip")
        {
            var bridge = Directory.CreateDirectory(Path.Combine(harness.ProjectDirectory, ".bridge")).FullName;
            var hop = Path.Combine(bridge, "hop.txt");
            File.CreateSymbolicLink(hop, target);
            File.CreateSymbolicLink(Path.Combine(source, "alias.txt"), hop);
        }
        else if (shape is "sibling-directory" or "nonmatching-intermediate")
            Directory.CreateSymbolicLink(Path.Combine(source, "alias"), targetDirectory);
        else
            File.CreateSymbolicLink(Path.Combine(source, "alias.txt"), target);

        var decision = await harness.EvaluateShellAsync($"touch '{harness.ProjectDirectory}'/{pattern}", Ct);

        Assert.Equal(allowed ? ApprovalOutcome.Allowed : ApprovalOutcome.RequiresApproval, decision.Outcome);
        if (allowed)
            Assert.Equal(ApprovalAllowReason.StoredApproval, decision.AllowReason);
        else
        {
            Assert.NotNull(decision.Prompt);
            Assert.Contains(ObservedOptionKeys.ApproveOnce, decision.Prompt.OptionKeys);
            Assert.DoesNotContain(ObservedOptionKeys.ApproveAlways, decision.Prompt.OptionKeys);
            Assert.DoesNotContain(ObservedOptionKeys.ApproveEverywhere, decision.Prompt.OptionKeys);
        }
        Assert.Equal("original", await File.ReadAllTextAsync(target, Ct));
    }

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
    public async Task An_unresolved_alias_does_not_reuse_a_global_grant(string shape, bool interactive)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("touch"), interactive);
        var alias = Path.Combine(harness.ProjectDirectory, "alias");
        var target = shape switch
        {
            "outside" => Path.GetDirectoryName(harness.ProjectDirectory)!,
            "broken" => Path.Combine(harness.ProjectDirectory, "absent"),
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

    [SlopwatchSuppress("SW001", "The fixture requires native POSIX directory aliases.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The fixture requires POSIX directory aliases.")]
    public async Task A_cycle_retains_the_literal_failed_resolution_denial()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("touch"), true);
        var alias = Path.Combine(harness.ProjectDirectory, "alias");
        Directory.CreateSymbolicLink(alias, alias);

        var literal = await harness.EvaluateShellAsync($"touch '{alias}/item.txt'", Ct);
        var glob = await harness.EvaluateShellAsync($"touch '{harness.ProjectDirectory}'/a*/*.txt", Ct);

        Assert.Equal(ApprovalOutcome.Denied, literal.Outcome);
        Assert.Equal("shell_references_protected_path", literal.DenyReason);
        Assert.Equal(literal.Outcome, glob.Outcome);
        Assert.Equal(literal.DenyReason, glob.DenyReason);
        Assert.Null(glob.Prompt);
        Assert.Equal(0, glob.ApprovalChecks);
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
