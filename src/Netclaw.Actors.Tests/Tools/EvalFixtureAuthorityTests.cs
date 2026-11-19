// -----------------------------------------------------------------------
// <copyright file="EvalFixtureAuthorityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class EvalFixtureAuthorityTests(ShellApprovalMatrixFixture fixture, ITestOutputHelper output) : IDisposable
{
    private const string BroadFixture = "netclaw.json";
    private const string BoundedFixture = "netclaw-bounded.json";
    private readonly DisposableTempDir _directory = new();

    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShellApprovalHost Host => OperatingSystem.IsWindows()
        ? ShellApprovalHost.WindowsPowerShell51
        : ShellApprovalHost.Bash52;

    [Theory]
    [InlineData(BroadFixture, false, false)]
    [InlineData(BroadFixture, false, true)]
    [InlineData(BroadFixture, true, false)]
    [InlineData(BroadFixture, true, true)]
    [InlineData(BoundedFixture, false, false)]
    [InlineData(BoundedFixture, false, true)]
    [InlineData(BoundedFixture, true, false)]
    [InlineData(BoundedFixture, true, true)]
    public async Task Bound_fixture_requires_an_explicit_shell_override_for_auto(
        string fixtureName, bool explicitAuto, bool interactive)
    {
        var tools = BindFixture(fixtureName, explicitAuto);
        await using var harness = await CreateHarnessAsync(tools, interactive);

        var decision = await harness.EvaluateShellAsync("netclaw status", Ct);

        output.WriteLine($"Status: {decision.Outcome}; allow={decision.AllowReason}; deny={decision.DenyReason}; checks={decision.ApprovalChecks}");
        Assert.Empty(decision.ApprovalMatches);
        if (explicitAuto)
        {
            Assert.Equal(ApprovalOutcome.Allowed, decision.Outcome);
            Assert.Equal(ApprovalAllowReason.PolicyAuto, decision.AllowReason);
            Assert.Equal(0, decision.ApprovalChecks);
            Assert.Null(decision.Prompt);
        }
        else
        {
            Assert.Equal(interactive ? ApprovalOutcome.RequiresApproval : ApprovalOutcome.Denied, decision.Outcome);
            Assert.Equal(interactive ? null : "approval_required_unattended", decision.DenyReason);
            Assert.Null(decision.AllowReason);
            Assert.Equal(1, decision.ApprovalChecks);
        }
    }

    [Theory]
    [InlineData(BroadFixture)]
    [InlineData(BoundedFixture)]
    public async Task Explicit_fixture_auto_preserves_a_configured_hard_denial(string fixtureName)
    {
        var tools = BindFixture(fixtureName, explicitAuto: true);
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "eval-fixture-hard-deny",
            new ShellApprovalInvocation("true", Interactive: false, Host: Host),
            Approvals.None, fixture.ActorSystem, Ct,
            policy: new ShellApprovalHarnessPolicy
            {
                HardDenyPatterns = ["fixture_forbidden_command"],
                ConfigureTools = config => config.AudienceProfiles = tools.AudienceProfiles
            });

        var decision = await harness.EvaluateShellAsync("fixture_forbidden_command", Ct);

        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal("hard_deny_custom_deny", decision.DenyReason);
        Assert.Equal(0, decision.ApprovalChecks);
        Assert.Null(decision.AllowReason);
    }

    [Theory]
    [InlineData(BroadFixture)]
    [InlineData(BoundedFixture)]
    public async Task Explicit_fixture_auto_preserves_a_protected_path_denial(string fixtureName)
    {
        var protectedPath = Path.Combine(_directory.Path, "protected.txt");
        await File.WriteAllTextAsync(protectedPath, "private fixture data", Ct);
        var tools = BindFixture(fixtureName, explicitAuto: true);
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "eval-fixture-protected",
            new ShellApprovalInvocation("true", Interactive: false, Host: Host),
            Approvals.None, fixture.ActorSystem, Ct,
            deniedPaths: [protectedPath],
            policy: new ShellApprovalHarnessPolicy
            {
                ConfigureTools = config => config.AudienceProfiles = tools.AudienceProfiles
            });
        var command = OperatingSystem.IsWindows()
            ? $"Get-Content '{protectedPath}'"
            : $"cat '{protectedPath}'";

        var decision = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
        Assert.Equal(0, decision.ApprovalChecks);
    }

    [SlopwatchSuppress("SW001", "The eval case uses native POSIX Bash paths and the literal /tmp directory.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The eval case requires POSIX Bash paths.")]
    [InlineData(BroadFixture, false)]
    [InlineData(BroadFixture, true)]
    [InlineData(BoundedFixture, false)]
    [InlineData(BoundedFixture, true)]
    public async Task Natural_directory_change_respects_the_fixture_file_authority(string fixtureName, bool explicitAuto)
    {
        var tools = BindFixture(fixtureName, explicitAuto);
        await using var harness = await CreateHarnessAsync(tools, interactive: false);

        var decision = await harness.EvaluateShellAsync("cd /tmp && pwd", Ct);

        output.WriteLine($"Directory: {decision.Outcome}; allow={decision.AllowReason}; deny={decision.DenyReason}; checks={decision.ApprovalChecks}");
        if (fixtureName == BoundedFixture)
        {
            Assert.Equal(ApprovalOutcome.Denied, decision.Outcome);
            Assert.Equal("shell_path_outside_trusted_roots", decision.DenyReason);
            Assert.Equal(0, decision.ApprovalChecks);
        }
        else
        {
            Assert.Equal(ApprovalOutcome.Allowed, decision.Outcome);
            Assert.Equal(explicitAuto ? ApprovalAllowReason.PolicyAuto : ApprovalAllowReason.ReviewedSafePolicy,
                decision.AllowReason);
        }
    }

    public void Dispose() => _directory.Dispose();

    private Task<ShellApprovalHarness> CreateHarnessAsync(ToolConfig tools, bool interactive)
    {
        var sessionDirectory = Directory.CreateDirectory(Path.Combine(_directory.Path, "session")).FullName;
        // The eval starts inside its own session. No project declaration precedes this case.
        var scope = new ShellApprovalHarnessScope(sessionDirectory, sessionDirectory, "signalr/eval-fixture", []);
        return ShellApprovalHarness.CreateAsync(
            "eval-fixture-authority",
            new ShellApprovalInvocation("true", Interactive: interactive, Host: Host),
            Approvals.None, fixture.ActorSystem, Ct,
            scope: scope,
            policy: new ShellApprovalHarnessPolicy
            {
                ConfigureTools = config => config.AudienceProfiles = tools.AudienceProfiles
            });
    }

    private ToolConfig BindFixture(string fixtureName, bool explicitAuto)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EvalFixtures", fixtureName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var overrides = json["Tools"]!["AudienceProfiles"]!["Personal"]!["ApprovalPolicy"]!["ToolOverrides"]!.AsObject();
        Assert.Equal("Auto", overrides["shell_execute"]!.GetValue<string>());
        if (!explicitAuto)
            Assert.True(overrides.Remove("shell_execute"));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonStream(stream)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:DeploymentPosture"] = "Personal",
                ["Security:ShellExecutionMode"] = "HostAllowed",
                ["Security:StrictDefaults"] = "false",
                ["Tools:ShellMode"] = "HostAllowed"
            })
            .Build();
        var tools = PolicyConfiguration.Bind(configuration).Tools;
        var profile = tools.AudienceProfiles.Personal;
        var approval = Assert.IsType<ToolApprovalConfig>(profile.ApprovalPolicy);
        Assert.Equal(ToolApprovalMode.Auto, approval.DefaultMode);
        Assert.Equal(explicitAuto ? ToolApprovalMode.Auto : ToolApprovalMode.Approval,
            approval.ToolOverrides["shell_execute"]);
        var filesystemMode = fixtureName == BoundedFixture ? ToolFilesystemMode.Roots : ToolFilesystemMode.All;
        Assert.Equal(filesystemMode, profile.ReadFiles.Mode);
        Assert.Equal(filesystemMode, profile.WriteFiles.Mode);
        Assert.Contains(ToolAudienceProfileDefaults.SkillsDirectoryToken, tools.AudienceProfiles.GlobalReadRoots);
        Assert.Contains(ToolAudienceProfileDefaults.IdentityDirectoryToken, tools.AudienceProfiles.GlobalReadRoots);
        output.WriteLine($"Fixture={fixtureName}; mode={approval.ToolOverrides["shell_execute"]}; read={profile.ReadFiles.Mode}; write={profile.WriteFiles.Mode}");
        return tools;
    }
}
