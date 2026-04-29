// -----------------------------------------------------------------------
// <copyright file="ToolApprovalGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    private const string ControlPlaneRoot = "/home/user/.netclaw/config";

    private static ToolAccessPolicy CreateFileWritePolicy(ToolApprovalConfig? approvalPolicy = null)
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = approvalPolicy;
        return new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            fileApprovalMatcher: new FilePathApprovalMatcher(ControlPlaneRoot));
    }

    private static INetclawTool FileWriteToolInstance() => new FileWriteTool();
    private static INetclawTool FileEditToolInstance() => new FileEditTool();

    [Fact]
    public void file_write_to_netclaw_json_requires_approval_under_fail_closed_default()
    {
        var policy = CreateFileWritePolicy(approvalPolicy: null);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ControlPlaneRoot + "/netclaw.json",
            ["Content"] = "{}"
        };

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Contains(
            decision.ApprovalContext!.UnapprovedPatterns,
            p => p.StartsWith("file_write:control-plane:", StringComparison.Ordinal));
    }

    [Fact]
    public void file_write_to_control_plane_still_requires_approval_when_policy_exists_without_override()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var policy = CreateFileWritePolicy(approvalPolicy);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ControlPlaneRoot + "/netclaw.json",
            ["Content"] = "{}"
        };

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Contains(
            decision.ApprovalContext!.UnapprovedPatterns,
            p => p.StartsWith("file_write:control-plane:", StringComparison.Ordinal));
    }

    [Fact]
    public void file_write_to_non_control_plane_path_auto_approves_under_null_policy()
    {
        var policy = CreateFileWritePolicy(approvalPolicy: null);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = "/tmp/scratch.txt",
            ["Content"] = "hello"
        };

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }

    [Fact]
    public void file_edit_of_netclaw_json_requires_approval()
    {
        var policy = CreateFileWritePolicy(approvalPolicy: null);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ControlPlaneRoot + "/netclaw.json",
            ["OldString"] = "a",
            ["NewString"] = "b"
        };

        var decision = policy.AuthorizeInvocation(FileEditToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.Contains(
            decision.ApprovalContext!.UnapprovedPatterns,
            p => p.StartsWith("file_edit:control-plane:", StringComparison.Ordinal));
    }

    [Fact]
    public void file_write_emits_distinct_per_path_patterns()
    {
        var matcher = new FilePathApprovalMatcher(ControlPlaneRoot);
        var netclawJson = matcher.ExtractPatterns(new ToolName("file_write"),
            new Dictionary<string, object?> { ["Path"] = ControlPlaneRoot + "/netclaw.json" });
        var toolApprovals = matcher.ExtractPatterns(new ToolName("file_write"),
            new Dictionary<string, object?> { ["Path"] = ControlPlaneRoot + "/tool-approvals.json" });
        var devices = matcher.ExtractPatterns(new ToolName("file_write"),
            new Dictionary<string, object?> { ["Path"] = ControlPlaneRoot + "/devices.json" });

        Assert.NotEqual(netclawJson[0], toolApprovals[0]);
        Assert.NotEqual(netclawJson[0], devices[0]);
        Assert.NotEqual(toolApprovals[0], devices[0]);
    }

    [Fact]
    public void file_write_control_plane_approval_honors_explicit_auto_override()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["file_write:control-plane"] = ToolApprovalMode.Auto
            }
        };
        var policy = CreateFileWritePolicy(approvalPolicy);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ControlPlaneRoot + "/netclaw.json",
            ["Content"] = "{}"
        };

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }

    [Fact]
    public void file_write_control_plane_override_takes_precedence_over_base_tool_override()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["file_write"] = ToolApprovalMode.Auto,
                ["file_write:control-plane"] = ToolApprovalMode.Approval
            }
        };
        var policy = CreateFileWritePolicy(approvalPolicy);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ControlPlaneRoot + "/netclaw.json",
            ["Content"] = "{}"
        };

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
    }

    [Fact]
    public void file_write_control_plane_falls_back_to_base_tool_override_when_specific_override_missing()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Approval,
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["file_write"] = ToolApprovalMode.Auto
            }
        };
        var policy = CreateFileWritePolicy(approvalPolicy);
        var args = new Dictionary<string, object?>
        {
            ["Path"] = ControlPlaneRoot + "/netclaw.json",
            ["Content"] = "{}"
        };

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }

    // ── MCP server default precedence ──

    private static ToolAccessPolicy CreateMcpApprovalPolicy(ToolApprovalConfig approvalPolicy)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.AllowedMcpServers.Add("notion");
        config.AudienceProfiles.Personal.ApprovalPolicy = approvalPolicy;
        return new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));
    }

    private static McpToolAdapter McpTool(string serverName, string toolName)
    {
        var func = AIFunctionFactory.Create(() => "result", toolName, toolName);
        return new McpToolAdapter(func, serverName, toolName);
    }

    [Fact]
    public void mcp_server_default_applies_when_no_exact_override()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Approval
            }
        };
        var policy = CreateMcpApprovalPolicy(approvalPolicy);

        var decision = policy.AuthorizeInvocation(
            McpTool("notion", "create-pages"),
            PersonalContext());

        Assert.True(decision.NeedsApproval);
        Assert.Equal("notion/create-pages", decision.ApprovalContext!.ToolName);
    }

    [Fact]
    public void mcp_exact_override_beats_server_default()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Deny
            },
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion/search"] = ToolApprovalMode.Auto
            }
        };
        var policy = CreateMcpApprovalPolicy(approvalPolicy);

        var decision = policy.AuthorizeInvocation(
            McpTool("notion", "search"),
            PersonalContext());

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }

    [Fact]
    public void mcp_server_default_beats_default_mode()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Deny
            }
        };
        var policy = CreateMcpApprovalPolicy(approvalPolicy);

        var decision = policy.AuthorizeInvocation(
            McpTool("notion", "create-pages"),
            PersonalContext());

        Assert.False(decision.Allowed);
        Assert.Equal("tool_denied_by_approval_policy", decision.DenyReason);
    }

    [Fact]
    public void shell_execute_does_not_match_mcp_server_default()
    {
        // shell_execute has no slash, so the MCP server default lookup
        // SHALL NOT trigger. The existing fail-closed-on-Personal matcher
        // must still fire instead.
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Deny
            }
        };
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = approvalPolicy;
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));

        var args = new Dictionary<string, object?> { ["Command"] = "git pull --ff-only" };
        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        // Shell default matcher fails closed on Personal → approval, not deny.
        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void resolve_approval_mode_and_get_effective_mode_agree_for_mcp_tool()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Approval
            },
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion/search"] = ToolApprovalMode.Auto
            }
        };

        // GetEffectiveMode is the single source of truth.
        Assert.Equal(ToolApprovalMode.Approval, approvalPolicy.GetEffectiveMode("notion/create-pages"));
        Assert.Equal(ToolApprovalMode.Auto, approvalPolicy.GetEffectiveMode("notion/search"));

        // Runtime path through ToolAccessPolicy must return the same modes.
        var policy = CreateMcpApprovalPolicy(approvalPolicy);

        var approval = policy.AuthorizeInvocation(McpTool("notion", "create-pages"), PersonalContext());
        Assert.True(approval.NeedsApproval);

        var auto = policy.AuthorizeInvocation(McpTool("notion", "search"), PersonalContext());
        Assert.True(auto.Allowed);
        Assert.False(auto.NeedsApproval);
    }

    [Fact]
    public void mcp_tool_without_server_default_falls_through_to_default_mode()
    {
        var approvalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto
        };
        var policy = CreateMcpApprovalPolicy(approvalPolicy);

        var decision = policy.AuthorizeInvocation(
            McpTool("notion", "create-pages"),
            PersonalContext());

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }
}
