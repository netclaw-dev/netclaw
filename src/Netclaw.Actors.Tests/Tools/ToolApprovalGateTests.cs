using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolApprovalGateTests
{
    private static ToolAccessPolicy CreatePolicy(ToolApprovalMode shellApprovalMode)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = shellApprovalMode
            }
        };

        return new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));
    }

    private static ToolExecutionContext PersonalContext(bool supportsApproval = true, string sessionId = "signalr/thread-1") =>
        new(sessionId, null) { Audience = "personal", SupportsInteractiveApproval = supportsApproval };

    private static INetclawTool ShellTool()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        return new ShellTool(config);
    }

    [Fact]
    public void Shell_in_approval_mode_returns_RequiresApproval_when_unapproved()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = new Dictionary<string, object?> { ["Command"] = "git push origin main" };

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Equal("shell_execute", decision.ApprovalContext!.ToolName);
        Assert.Contains("git push", decision.ApprovalContext.UnapprovedPatterns);
    }

    [Fact]
    public void Shell_in_deny_mode_returns_deny()
    {
        var policy = CreatePolicy(ToolApprovalMode.Deny);
        var args = new Dictionary<string, object?> { ["Command"] = "git push" };

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.False(decision.Allowed);
        Assert.Equal("tool_denied_by_approval_policy", decision.DenyReason);
    }

    [Fact]
    public void Shell_in_auto_mode_allows_without_approval()
    {
        var policy = CreatePolicy(ToolApprovalMode.Auto);
        var args = new Dictionary<string, object?> { ["Command"] = "git push" };

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }

    [Fact]
    public void Missing_personal_approval_policy_fails_closed_for_shell()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = null;

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));

        var args = new Dictionary<string, object?> { ["Command"] = "git pull --ff-only" };

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Equal("shell_execute", decision.ApprovalContext!.ToolName);
    }

    [Fact]
    public void Unsupported_channel_auto_denies()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = new Dictionary<string, object?> { ["Command"] = "git push" };

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(supportsApproval: false), args);

        Assert.False(decision.Allowed);
        Assert.Equal("channel_does_not_support_approval", decision.DenyReason);
    }

    [Fact]
    public void Compound_command_surfaces_all_approval_patterns_for_service_filtering()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = new Dictionary<string, object?> { ["Command"] = "git add . && git commit -m fix && git push" };

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.Contains("git add", decision.ApprovalContext!.UnapprovedPatterns);
        Assert.Contains("git commit", decision.ApprovalContext!.UnapprovedPatterns);
        Assert.Contains("git push", decision.ApprovalContext.UnapprovedPatterns);
    }

    [Fact]
    public void Hard_denied_shell_command_is_blocked_before_approval()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            new Dictionary<string, object?> { ["Command"] = "netclaw daemon stop" });

        Assert.False(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal("hard_deny_self_destructive", decision.DenyReason);
    }
}
