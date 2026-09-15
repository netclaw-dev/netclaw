// -----------------------------------------------------------------------
// <copyright file="WindowsPowerShellTreeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class WindowsPowerShellTreeApprovalTests(
    ShellApprovalMatrixFixture fixture)
{
    private const string RecursiveCommand =
        "Get-ChildItem -Path \"C:\\WORK\\PROJECT\" -Recurse "
        + "-Include *.cs,*.conf,*.json | Select-Object -ExpandProperty FullName";

    private static readonly ShellApprovalHarnessScope Scope = new(
        @"C:\WORK\PROJECT",
        @"C:\WORK\SESSION",
        "signalr/tree-approval",
        []);

    [Theory]
    [InlineData(ToolApprovalMode.Auto)]
    [InlineData(ToolApprovalMode.Approval)]
    public async Task Interactive_windows_recursive_tree_is_once_or_deny_only(
        ToolApprovalMode mode)
    {
        await using var harness = await CreateHarness(
            RecursiveCommand,
            ShellApprovalHost.WindowsPowerShell51,
            mode,
            interactive: true,
            Approvals.None);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.True(approval.IsMessy);
        Assert.Empty(approval.Candidates!);
        Assert.NotEmpty(approval.Patterns);
        Assert.Collection(
            approval.Options,
            option => Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, option.Key),
            option => Assert.Equal(ApprovalOptionKeys.DenyKey, option.Key));
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public async Task Noninteractive_auto_denies_windows_recursive_tree()
    {
        await using var harness = await CreateHarness(
            RecursiveCommand,
            ShellApprovalHost.WindowsPowerShell51,
            ToolApprovalMode.Auto,
            interactive: false,
            Approvals.None);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_unresolved_trust_zone_input", decision.DenyReason);
        Assert.Null(decision.ApprovalContext);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public async Task Deny_mode_denies_windows_recursive_tree()
    {
        await using var harness = await CreateHarness(
            RecursiveCommand,
            ShellApprovalHost.WindowsPowerShell51,
            ToolApprovalMode.Deny,
            interactive: true,
            Approvals.None);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("tool_denied_by_approval_policy", decision.DenyReason);
        Assert.Null(decision.ApprovalContext);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public async Task Stored_and_session_grants_do_not_cover_windows_recursive_tree()
    {
        var grants = Approvals.Combine(
            Approvals.Session("Get-ChildItem", "Select-Object"),
            Approvals.PersistentAnywhere("Get-ChildItem", "Select-Object"));
        await using var harness = await CreateHarness(
            RecursiveCommand,
            ShellApprovalHost.WindowsPowerShell51,
            ToolApprovalMode.Approval,
            interactive: true,
            grants);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.Empty(decision.ApprovalMatches);
        Assert.Empty(decision.ApprovalContext!.Candidates!);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public async Task Exact_tree_one_time_retry_reparses_and_allows_only_that_invocation()
    {
        await using var harness = await CreateHarness(
            RecursiveCommand,
            ShellApprovalHost.WindowsPowerShell51,
            ToolApprovalMode.Approval,
            interactive: true,
            Approvals.None);

        var initial = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);
        harness.SeedOneTimeApproval(initial.ApprovalContext!);
        var retry = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, retry.Outcome);
        Assert.Equal(ToolAllowReason.OneTimeApproval, retry.AllowReason);
        Assert.Empty(retry.ApprovalMatches);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [Theory]
    [InlineData(
        nameof(ShellApprovalHost.PowerShell7),
        "Get-ChildItem -Path \"C:\\WORK\\PROJECT\" -Recurse -Include *.cs,*.conf,*.json | Select-Object -ExpandProperty FullName")]
    [InlineData(
        nameof(ShellApprovalHost.WindowsPowerShell51),
        "Get-Content \"C:\\WORK\\PROJECT\\SourceFile.cs\" | Select-Object -Index (113..145)")]
    [InlineData(
        nameof(ShellApprovalHost.WindowsPowerShell51),
        "Select-String -Path \"C:\\WORK\\PROJECT\\SourceFile.cs\" -Pattern needle | Select-Object LineNumber,Line")]
    [InlineData(
        nameof(ShellApprovalHost.WindowsPowerShell51),
        "Get-Process -Name dotnet,powershell | Select-Object Id,ProcessName,StartTime")]
    public async Task Bounded_live_read_shapes_use_reviewed_safe_policy(
        string hostName,
        string command)
    {
        var host = Enum.Parse<ShellApprovalHost>(hostName);
        await using var harness = await CreateHarness(
            command,
            host,
            ToolApprovalMode.Approval,
            interactive: true,
            Approvals.None);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(ToolAllowReason.ReviewedSafePolicy, decision.AllowReason);
        Assert.Equal(1, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public async Task Hard_deny_precedes_seeded_exact_tree_retry()
    {
        var command = RecursiveCommand + "; Stop-Process -Name netclaw";
        await using var harness = await CreateHarness(
            command,
            ShellApprovalHost.WindowsPowerShell51,
            ToolApprovalMode.Auto,
            interactive: true,
            Approvals.None,
            Scope with { OneTimeApprovalKeys = CreateExactApprovalKeys(command) });

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("hard_deny_self_destructive", decision.DenyReason);
        Assert.Null(decision.ApprovalContext);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    [Fact]
    public async Task Protected_tree_root_precedes_seeded_exact_tree_retry()
    {
        const string command =
            "Get-ChildItem -Path \"C:\\WORK\\PROJECT\\restricted\" -Recurse";
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "protected-tree-root",
            new ShellApprovalInvocation(
                command,
                Interactive: true,
                Host: ShellApprovalHost.WindowsPowerShell51),
            Approvals.None,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            scope: Scope with { OneTimeApprovalKeys = CreateExactApprovalKeys(command) },
            deniedPaths: [@"C:\WORK\PROJECT\restricted"],
            shellApprovalMode: ToolApprovalMode.Auto);

        var decision = await harness.EvaluateDecisionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
        Assert.Null(decision.ApprovalContext);
        Assert.Equal(0, harness.ApprovalService.CheckCount);
    }

    private async Task<ShellApprovalHarness> CreateHarness(
        string command,
        ShellApprovalHost host,
        ToolApprovalMode mode,
        bool interactive,
        ApprovalState approvals,
        ShellApprovalHarnessScope? scope = null)
        => await ShellApprovalHarness.CreateAsync(
            "windows-tree-approval",
            new ShellApprovalInvocation(
                command,
                Interactive: interactive,
                Host: host),
            approvals,
            fixture.ActorSystem,
            TestContext.Current.CancellationToken,
            scope: scope ?? Scope,
            shellApprovalMode: mode);

    private static IReadOnlyList<string> CreateExactApprovalKeys(string command)
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);
        var approval = new ShellApprovalMatcher(environment).AnalyzeInvocation(
            new ToolName(ShellTool.ToolName),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = Scope.ProjectDirectory
            });
        return OneTimeApprovalKeys.Create(
            approval.Patterns,
            approval.Candidates,
            Scope.ProjectDirectory);
    }
}
