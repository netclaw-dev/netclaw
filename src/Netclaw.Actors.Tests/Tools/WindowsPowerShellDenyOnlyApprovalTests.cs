// -----------------------------------------------------------------------
// <copyright file="WindowsPowerShellDenyOnlyApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Protocol;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class WindowsPowerShellDenyOnlyApprovalTests
{
    private static readonly ShellExecutionEnvironment Environment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);

    [Theory]
    [InlineData(ToolApprovalMode.Auto, "netclaw daemon stop; $item++")]
    [InlineData(ToolApprovalMode.Auto, "$item++; netclaw daemon stop")]
    [InlineData(ToolApprovalMode.Approval, "netclaw daemon stop; $item++")]
    [InlineData(ToolApprovalMode.Approval, "$item++; netclaw daemon stop")]
    public void Partial_deny_precedes_auto_and_approval_modes(
        ToolApprovalMode mode,
        string command)
    {
        var (policy, tool) = CreatePolicy(mode);

        var decision = policy.GetShellPreflightDecision(
            tool,
            PersonalContext(),
            ToolInput.Create("Command", command));

        Assert.False(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal("hard_deny_self_destructive", decision.DenyReason);
    }

    [Fact]
    public void Auto_mode_keeps_an_unresolved_non_deny_call_auto()
    {
        var (policy, tool) = CreatePolicy(ToolApprovalMode.Auto);

        var preflight = policy.AuthorizeShellPreflight(
            tool,
            PersonalContext(),
            ToolInput.Create("Command", "Write-Output ok; $item++"));

        var complete = Assert.IsType<ShellPolicyPreflightResult.Complete>(preflight);
        var execution = Assert.IsType<ToolAuthorizationResult.ShellExecution>(complete.Result);
        Assert.True(execution.Decision.Allowed);
        Assert.False(execution.Decision.NeedsApproval);
        Assert.Equal(ToolAllowReason.PolicyAuto, execution.Decision.AllowReason);
        Assert.False(execution.Analysis.IsResolved);
    }

    [Fact]
    public void Approval_mode_keeps_an_unresolved_non_deny_call_one_shot_only()
    {
        var (policy, tool) = CreatePolicy(ToolApprovalMode.Approval);

        var decision = policy.GetShellPreflightDecision(
            tool,
            PersonalContext(),
            ToolInput.Create("Command", "Write-Output ok; $item++"));

        Assert.True(decision.NeedsApproval);
        var approval = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.True(approval.IsMessy);
        Assert.Empty(approval.Patterns);
        Assert.Empty(approval.Candidates!);
        Assert.Collection(
            approval.Options,
            option => Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, option.Key),
            option => Assert.Equal(ApprovalOptionKeys.DenyKey, option.Key));
    }

    private static (ToolAccessPolicy Policy, ShellTool Tool) CreatePolicy(
        ToolApprovalMode mode)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ShellTool.ToolName] = mode
            }
        };
        var commandPolicy = new ShellCommandPolicy(Environment);
        var pathPolicy = new ToolPathPolicy(Environment, []);
        var policy = new ToolAccessPolicy(
            new NetclawPaths(),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy);
        return (policy, new ShellTool(config, pathPolicy, commandPolicy));
    }

    private static ToolExecutionContext PersonalContext()
        => TestToolExecutionContext.CreateBound(
            "signalr/powershell-deny-only",
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });
}
