// -----------------------------------------------------------------------
// <copyright file="ToolApprovalGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolApprovalGateTests
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

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
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));
    }

    private static ToolExecutionContext PersonalContext(bool supportsApproval = true, string sessionId = "signalr/thread-1") =>
        TestToolExecutionContext.CreateBound(sessionId, null, new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal, InteractiveApproval = TestToolExecutionContext.InteractiveApproval(supportsApproval) });

    private static INetclawTool ShellTool()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        return new ShellTool(config, new ToolPathPolicy([]), new ShellCommandPolicy());
    }

    [Fact]
    public void Shell_in_deny_mode_returns_deny()
    {
        var policy = CreatePolicy(ToolApprovalMode.Deny);
        var args = ToolInput.Create("Command", "git push");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.False(decision.Allowed);
        Assert.Equal("tool_denied_by_approval_policy", decision.DenyReason);
    }

    [Fact]
    public void Shell_in_auto_mode_allows_without_approval()
    {
        var policy = CreatePolicy(ToolApprovalMode.Auto);
        var args = ToolInput.Create("Command", "git push");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal(ToolAllowReason.PolicyAuto, decision.AllowReason);
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
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            ToolInput.Create("Command", "git pull --ff-only"));

        Assert.True(decision.NeedsApproval);
        Assert.Equal("shell_execute", decision.ApprovalContext!.ToolName);
    }

    [SlopwatchSuppress("SW001", "This test verifies Bash glob behavior, which does not apply to the Windows shell parser.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Static_shell_glob_uses_covering_directory_and_offers_persistent_approval()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create(
            "Command", "rm /tmp/*.bak",
            "WorkingDirectory", "/home/user/project");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.False(decision.ApprovalContext!.IsMessy);
        var candidate = Assert.Single(decision.ApprovalContext.Candidates!);
        Assert.Equal("rm", candidate.Verb);
        Assert.Equal("/tmp", candidate.Directory);
        Assert.Contains(
            decision.ApprovalContext.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveAlways);
    }

    [Fact]
    public void Compound_command_surfaces_all_approval_patterns_for_service_filtering()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create("Command", "git add . && git commit -m fix && git push");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.Contains("git add .", decision.ApprovalContext!.Patterns);
        Assert.Contains("git commit -m fix", decision.ApprovalContext!.Patterns);
        Assert.Contains("git push", decision.ApprovalContext.Patterns);
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
            shellCommandPolicy: new ShellCommandPolicy(),
            toolPathPolicy: new ToolPathPolicy([]),
            fileApprovalMatcher: new FilePathApprovalMatcher(ControlPlaneRoot));
    }

    private static INetclawTool FileWriteToolInstance() => new FileWriteTool(new ToolConfig(), new NetclawPaths(), new ToolPathPolicy([]));
    private static INetclawTool FileEditToolInstance() => new FileEditTool(new ToolConfig(), new NetclawPaths(), new ToolPathPolicy([]));

    [Fact]
    public void file_write_to_netclaw_json_requires_approval_under_fail_closed_default()
    {
        var policy = CreateFileWritePolicy(approvalPolicy: null);
        var args = ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json", "Content", "{}");

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Contains(
            decision.ApprovalContext!.Patterns,
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
        var args = ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json", "Content", "{}");

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Contains(
            decision.ApprovalContext!.Patterns,
            p => p.StartsWith("file_write:control-plane:", StringComparison.Ordinal));
    }

    [Fact]
    public void file_write_to_non_control_plane_path_auto_approves_under_null_policy()
    {
        var policy = CreateFileWritePolicy(approvalPolicy: null);
        var args = ToolInput.Create("Path", "/tmp/scratch.txt", "Content", "hello");

        var decision = policy.AuthorizeInvocation(FileWriteToolInstance(), PersonalContext(), args);

        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
    }

    [Fact]
    public void file_edit_of_netclaw_json_requires_approval()
    {
        var policy = CreateFileWritePolicy(approvalPolicy: null);
        var args = ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json", "OldString", "a", "NewString", "b");

        var decision = policy.AuthorizeInvocation(FileEditToolInstance(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.Contains(
            decision.ApprovalContext!.Patterns,
            p => p.StartsWith("file_edit:control-plane:", StringComparison.Ordinal));
    }

    [Fact]
    public void file_write_emits_distinct_per_path_patterns()
    {
        var matcher = new FilePathApprovalMatcher(ControlPlaneRoot);
        var netclawJson = matcher.ExtractPatterns(new ToolName("file_write"),
            ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json"));
        var toolApprovals = matcher.ExtractPatterns(new ToolName("file_write"),
            ToolInput.Create("Path", ControlPlaneRoot + "/tool-approvals.json"));
        var devices = matcher.ExtractPatterns(new ToolName("file_write"),
            ToolInput.Create("Path", ControlPlaneRoot + "/devices.json"));

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
        var args = ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json", "Content", "{}");

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
        var args = ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json", "Content", "{}");

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
        var args = ToolInput.Create("Path", ControlPlaneRoot + "/netclaw.json", "Content", "{}");

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
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));
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
    public void mcp_approval_context_displays_arguments_without_netclaw_meta_fields()
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
        var content = string.Join('\n', Enumerable.Repeat("large memory payload", 1_000));

        var decision = policy.AuthorizeInvocation(
            McpTool("notion", "create-pages"),
            PersonalContext(),
            new Dictionary<string, object?>
            {
                ["destination_path"] = "/Board/Quarterly Results",
                ["content"] = content,
                ["_rationale"] = "Create the requested report"
            });

        Assert.True(decision.NeedsApproval);
        Assert.Contains("destination_path=\"/Board/Quarterly Results\"", decision.ApprovalContext!.DisplayText);
        Assert.Contains($"content=({content.Length} chars, 1000 lines)", decision.ApprovalContext.DisplayText);
        Assert.DoesNotContain("_rationale", decision.ApprovalContext.DisplayText);
        Assert.DoesNotContain("Create the requested report", decision.ApprovalContext.DisplayText);
        Assert.DoesNotContain(
            decision.ApprovalContext.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveAlways);
        Assert.Contains(
            decision.ApprovalContext.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere
                      && option.Label == ApprovalOptionKeys.ApproveMcpToolLabel);
        Assert.DoesNotContain(
            decision.ApprovalContext.Options,
            option => option.Label == ApprovalOptionKeys.ApproveEverywhereLabel);
    }

    [Fact]
    public void mcp_declared_parameter_that_resembles_meta_remains_visible()
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
        var function = AIFunctionFactory.Create(
            (string rationale) => rationale,
            "create-pages",
            "create-pages");
        var tool = new McpToolAdapter(function, "notion", "create-pages");

        var decision = policy.AuthorizeInvocation(
            tool,
            PersonalContext(),
            new Dictionary<string, object?> { ["Rationale"] = "Customer-visible reason" });

        Assert.True(decision.NeedsApproval);
        Assert.Contains("Rationale=\"Customer-visible reason\"", decision.ApprovalContext!.DisplayText);
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
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));

        var args = ToolInput.Create("Command", "git pull --ff-only");
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

    // ── Subagent approval gate (SupportsInteractiveApproval=false) ──

    [Fact]
    public void Non_interactive_tool_requires_approval_when_policy_requires_approval()
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Approval
        };
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));

        var tool = new Netclaw.Actors.Tests.Memory.FakeNetclawTool("file_read", "content");
        var subagentCtx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, subagentCtx);

        // No safe-list auto-grant: the approval policy is authoritative for every
        // channel, so a non-interactive caller fails closed to requires-approval.
        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
    }

    [Fact]
    public void Non_shell_tool_omits_directory_scoped_persistence_option()
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Approval
        };
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));

        var tool = new Netclaw.Actors.Tests.Memory.FakeNetclawTool("file_read", "content");
        var decision = policy.AuthorizeInvocation(tool, PersonalContext());

        Assert.True(decision.NeedsApproval);
        Assert.DoesNotContain(
            decision.ApprovalContext!.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveAlways);
        Assert.Contains(
            decision.ApprovalContext.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveEverywhere
                      && option.Label == ApprovalOptionKeys.ApproveEverywhereLabel);
    }

    [Fact]
    public void Non_safe_list_tool_returns_requires_approval_when_interactive_unsupported()
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Approval
        };
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([]));

        var tool = new Netclaw.Actors.Tests.Memory.FakeNetclawTool("store_memory", "ok");
        var subagentCtx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, subagentCtx);

        // Non-interactive channels now fall through to RequiresApproval so the
        // executor can check the persistent approval store before denying.
        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Equal("store_memory", decision.ApprovalContext!.ToolName);
    }

    [Fact]
    public void Non_interactive_shell_with_path_outside_trust_zone_is_denied()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);
        var outsidePath = Path.Combine(dir.Path, "outside", "secrets.txt");

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?>
            {
                ["command"] = TestShellEnvironment.ReadFileCommand(outsidePath)
            });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_path_outside_trust_zone", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_path_inside_trust_zone_proceeds_to_approval()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);
        var insidePath = Path.Combine(trustRoot, "project", "README.md");

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?>
            {
                ["command"] = TestShellEnvironment.ReadFileCommand(insidePath)
            });

        // Path is within trust zone — proceeds to the approval gate (RequiresApproval)
        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Non_interactive_shell_without_path_args_proceeds_to_approval()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        // "git status" has no path-like arguments
        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "git status" });

        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Interactive_shell_skips_trust_zone_check()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: true);

        // Interactive channels don't enforce trust zones — the human approves
        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "cat /etc/passwd" });

        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Non_interactive_shell_with_nested_shell_path_outside_trust_zone_is_denied()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);
        var outsidePath = Path.Combine(dir.Path, "outside", "shadow.txt");

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = $"bash -c \"cat {outsidePath}\"" });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_path_outside_trust_zone", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_working_directory_outside_trust_zone_is_denied()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);
        var outsideDir = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(outsideDir);

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?>
            {
                ["command"] = "cat README.md",
                ["workingDirectory"] = outsideDir
            });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_working_directory_outside_trust_zone", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_in_zone_working_directory_and_relative_path_proceeds_to_approval()
    {
        using var dir = new DisposableTempDir();
        var trustRoot = CreateTrustZoneRoot(dir.Path);
        var workingDirectory = Path.Combine(trustRoot, "project");
        Directory.CreateDirectory(workingDirectory);

        var trustZone = new FakeShellTrustZonePolicy([trustRoot]);
        var policy = CreatePolicyWithTrustZone(trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?>
            {
                ["command"] = "cat README.md",
                ["workingDirectory"] = workingDirectory
            });

        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Non_interactive_shell_with_path_denied_when_no_trust_zone_configured()
    {
        // No trust zone policy = fail-closed for non-interactive path commands
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "cat /etc/passwd" });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_trust_zone_policy_not_configured", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_working_directory_denied_when_no_trust_zone_configured()
    {
        using var dir = new DisposableTempDir();
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?>
            {
                ["command"] = "cat README.md",
                ["workingDirectory"] = dir.Path
            });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_trust_zone_policy_not_configured", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_pathless_shell_no_longer_false_denies()
    {
        // Regression for #1244: the old empty-roots check denied EVERY
        // non-interactive Personal shell command (including path-less ones) with
        // shell_no_trust_zone_roots, because GetTrustZoneRoots returned [] for
        // Personal (WriteFiles.Mode == All). With the real policy a path-less
        // command now extracts no path tokens and falls through to the approval
        // gate. (Path-bearing out-of-zone commands are confined by the autonomous
        // zone — see AutonomousZoneClampTests.)
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        // Real policy — Personal profile resolves WriteFiles.Mode == All.
        var trustZone = new ShellTrustZonePolicy(config, new NetclawPaths());
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            shellCommandPolicy: new ShellCommandPolicy(),
            toolPathPolicy: new ToolPathPolicy([]),
            shellTrustZonePolicy: trustZone);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "git status" });

        Assert.Null(decision.DenyReason);
        Assert.True(decision.NeedsApproval);
    }

    private static string CreateTrustZoneRoot(string tempDir)
    {
        var root = Path.Combine(tempDir, ".netclaw", "workspaces");
        Directory.CreateDirectory(Path.Combine(root, "project"));
        return root;
    }

    private static ToolAccessPolicy CreatePolicyWithTrustZone(IShellTrustZonePolicy trustZone)
    {
        var environment = TestShellEnvironment.Current;
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        return new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            shellCommandPolicy: new ShellCommandPolicy(environment),
            toolPathPolicy: new ToolPathPolicy(environment, []),
            shellTrustZonePolicy: trustZone);
    }

    // Simulates a Mode.Roots audience: a path is write-authorized iff it falls
    // within one of the configured roots (the same IsWithinAnyRoot semantics the
    // real ScopedFileAccessPolicy applies for Mode.Roots).
    private sealed class FakeShellTrustZonePolicy : IShellTrustZonePolicy
    {
        private readonly IReadOnlyList<string> _roots;

        public FakeShellTrustZonePolicy(IReadOnlyList<string> roots) => _roots = roots;

        public bool IsShellWritePathAuthorized(string fullPath, ToolInvocationContext context)
            => PathUtility.IsWithinAnyRoot(fullPath, _roots);
    }

    // ── v2 candidate-verb extraction (replaces v1 directory-root extraction) ──

    [Fact]
    public void Shell_path_command_extracts_path_aware_verb_chain_with_no_directory_roots()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create("Command", "cat /home/user/.netclaw/logs/crash.log");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.NotNull(decision.ApprovalContext);
        // Under v2.1 path-extraction, the verb chain is the command head
        // only; the path is captured separately as the candidate's
        // directory (with the file-parent rule reducing the leaf file to
        // its parent directory).
        Assert.Contains("cat", decision.ApprovalContext!.CandidateVerbs);
        Assert.NotNull(decision.ApprovalContext.Candidates);
        var candidate = Assert.Single(decision.ApprovalContext.Candidates!);
        Assert.Equal("cat", candidate.Verb);
        Assert.NotNull(candidate.Directory);
        Assert.Equal(
            "/home/user/.netclaw/logs",
            candidate.Directory!.Replace('\\', '/'));
    }

    [Fact]
    public void Shell_path_command_uses_fixed_labels()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create("Command", "grep 'error' /home/user/.netclaw/logs/app.log");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        var options = decision.ApprovalContext!.Options;
        var sessionOption = options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveSession);
        var alwaysOption = options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveAlways);
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, sessionOption.Label);
        Assert.Equal(ApprovalOptionKeys.ApproveAlwaysLabel, alwaysOption.Label);
    }

    [Fact]
    public void Shell_multi_root_command_uses_fixed_labels()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create(
            "Command",
            "cat /netclaw-approval-test/logs/app.log > /netclaw-approval-test/output/report.txt");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        var options = decision.ApprovalContext!.Options;
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveSession).Label);
        Assert.Equal(ApprovalOptionKeys.ApproveAlwaysLabel, options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveAlways).Label);
    }

    [Fact]
    public void Shell_relative_path_command_extracts_verb_chain_without_directory_roots()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);

        try
        {
            var args = ToolInput.Create(
                "Command", "grep timeout logs/app.log | wc -l",
                "WorkingDirectory", root);

            var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

            Assert.True(decision.NeedsApproval);
            // Pipelines stay inside one approval unit, so the candidate is
            // the verb chain of the unit's first command (path-aware
            // "grep <first-arg>").
            Assert.Contains(decision.ApprovalContext!.CandidateVerbs, v => v.StartsWith("grep", StringComparison.Ordinal));
            // Button labels are fixed; Slack's 76-char and Discord's 80-char
            // button caps make dynamic labels structurally unsafe.
            Assert.Equal(
                ApprovalOptionKeys.ApproveSessionLabel,
                decision.ApprovalContext.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveSession).Label);
            Assert.Equal(
                ApprovalOptionKeys.ApproveAlwaysLabel,
                decision.ApprovalContext.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveAlways).Label);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Non_path_command_extracts_full_greedy_verb_chain()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create("Command", "git push origin main");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        // ExtractVerbChain (post-ShellSyntaxTree-0.1.4) extracts greedily
        // through every verb-like token. `origin` and `main` have no slash,
        // dot, or flag prefix, so the chain extends through both. This is
        // the operator-friendly contract: persisted approvals key on the
        // specific arg shape (`git push origin main *`) rather than the
        // verb family (`git push *`), making them safer by construction.
        Assert.Equal(["git push origin main"], decision.ApprovalContext!.CandidateVerbs);
        var sessionOption = decision.ApprovalContext.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveSession);
        var alwaysOption = decision.ApprovalContext.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveAlways);
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, sessionOption.Label);
        Assert.Equal(ApprovalOptionKeys.ApproveAlwaysLabel, alwaysOption.Label);
    }

    // Regression pin for issue #931 — long directory paths must not produce
    // labels that exceed `ApprovalOptionKeys.MaxLabelLength`.
    [Fact]
    public void Shell_command_with_long_directory_path_keeps_labels_within_button_caps()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var deepPath = "/home/user/repositories/petabridge/testlab-setup/services/kubernetes/ingress/configs/app.log";
        Assert.True(deepPath.Length > ApprovalOptionKeys.MaxLabelLength);

        var args = ToolInput.Create("Command", $"grep error {deepPath}");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        var options = decision.ApprovalContext!.Options;
        Assert.All(options, option => Assert.True(
            option.Label.Length <= ApprovalOptionKeys.MaxLabelLength,
            $"Option '{option.Key}' label '{option.Label}' is {option.Label.Length} chars; must stay within {ApprovalOptionKeys.MaxLabelLength}."));
        Assert.Equal(ApprovalOptionKeys.ApproveOnceLabel, options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveOnce).Label);
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveSession).Label);
        Assert.Equal(ApprovalOptionKeys.ApproveAlwaysLabel, options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveAlways).Label);
        Assert.Equal(ApprovalOptionKeys.DenyLabel, options.Single(o => o.Key.Value == ApprovalOptionKeys.Deny).Label);
    }
}
