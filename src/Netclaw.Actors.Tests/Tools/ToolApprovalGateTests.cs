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

    private static string ApprovalTestRoot { get; } = Path.Combine(
        Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))
        ?? throw new InvalidOperationException("The test process has no filesystem root."),
        "netclaw-approval-test");

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
            new NetclawPaths(),
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

    [Theory]
    [InlineData("shell_disabled", null, "Tool access denied: shell_disabled")]
    [InlineData("path_access_denied", "Error: Path is outside trusted roots.", "Error: Path is outside trusted roots.")]
    public void Access_denial_uses_one_agent_result_format(
        string reason,
        string? detail,
        string expected)
    {
        var exception = new ToolAccessDeniedException(reason, detail);

        Assert.Equal(expected, exception.ToAgentResult());
    }

    [Fact]
    public void Shell_in_deny_mode_returns_deny()
    {
        var policy = CreatePolicy(ToolApprovalMode.Deny);
        var context = PersonalContext();
        var args = ToolInput.Create("Command", "git push");

        var decision = policy.AuthorizeInvocation(ShellTool(), context, args);

        Assert.False(decision.Allowed);
        Assert.Equal("tool_denied_by_approval_policy", decision.DenyReason);
    }

    [Fact]
    public void Shell_in_auto_mode_allows_without_approval()
    {
        var policy = CreatePolicy(ToolApprovalMode.Auto);
        var args = ToolInput.Create("Command", "git push");

        var preflight = policy.AuthorizeShellPreflight(
            ShellTool(),
            PersonalContext(),
            args);

        var complete = Assert.IsType<ShellPolicyPreflightResult.Complete>(preflight);
        var decision = complete.Decision;
        Assert.True(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal(ToolAllowReason.PolicyAuto, decision.AllowReason);
        Assert.NotNull(complete.AuthorizedAnalysis);
        Assert.Equal("git push", complete.AuthorizedAnalysis.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shell_approval_without_command_preserves_prompt(bool includeNullCommand)
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var arguments = includeNullCommand
            ? ToolInput.Create("Command", null)
            : ToolInput.Empty();

        var preflight = policy.AuthorizeShellPreflight(
            ShellTool(),
            PersonalContext(),
            arguments);

        var complete = Assert.IsType<ShellPolicyPreflightResult.Complete>(preflight);
        Assert.True(complete.Decision.NeedsApproval);
        Assert.Null(complete.AuthorizedAnalysis);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Auto)]
    [InlineData(ToolApprovalMode.Deny)]
    [InlineData((ToolApprovalMode)999)]
    public void Shell_hard_deny_precedes_approval_mode(ToolApprovalMode mode)
    {
        var policy = CreatePolicy(mode);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            ToolInput.Create("Command", "rm -rf /"));

        Assert.False(decision.Allowed);
        Assert.Equal("hard_deny_system_destructive", decision.DenyReason);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Auto)]
    [InlineData(ToolApprovalMode.Deny)]
    [InlineData((ToolApprovalMode)999)]
    public void Shell_protected_path_precedes_approval_mode(ToolApprovalMode mode)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        const string protectedRoot = "/protected";
        var config = CreateShellConfig(mode);
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, [protectedRoot]);
        var policy = new ToolAccessPolicy(new NetclawPaths(), config, Defaults(), commandPolicy, pathPolicy);
        var tool = new ShellTool(config, pathPolicy, commandPolicy);

        var decision = policy.AuthorizeInvocation(
            tool,
            PersonalContext(),
            ToolInput.Create("Command", "cat /protected/secret.txt"));

        Assert.False(decision.Allowed);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Auto, "shell_path_outside_trust_zone")]
    [InlineData(ToolApprovalMode.Deny, "shell_path_outside_trust_zone")]
    public void Shell_approval_mode_preserves_unattended_path_authorization(
        ToolApprovalMode mode,
        string expectedDenyReason)
    {
        var policy = CreatePolicy(mode);
        var context = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            context,
            ToolInput.Create("Command", "cat /external/data.txt"));

        Assert.False(decision.Allowed);
        Assert.Equal(expectedDenyReason, decision.DenyReason);
        Assert.False(decision.NeedsApproval);
    }

    [Fact]
    public void Invalid_shell_approval_mode_fails_closed()
    {
        var policy = CreatePolicy((ToolApprovalMode)999);
        var context = PersonalContext();

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            context,
            ToolInput.Create("Command", "git status"));

        Assert.False(decision.Allowed);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
        Assert.Null(context.Cwd);
    }

    [Fact]
    public void Missing_personal_approval_policy_fails_closed_for_shell()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = null;
        var policy = new ToolAccessPolicy(new NetclawPaths(),
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
        using var dir = new DisposableTempDir();
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var args = ToolInput.Create(
            "Command", $"rm {dir.Path}/*.bak",
            "WorkingDirectory", dir.Path);

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        Assert.False(decision.ApprovalContext!.IsMessy);
        var candidate = Assert.Single(decision.ApprovalContext.Candidates!);
        Assert.Equal("rm", candidate.Verb);
        Assert.Equal(dir.Path, candidate.Directory);
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

    [Fact]
    public void Compound_command_keeps_distinct_typed_occurrences_with_one_legacy_projection()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            ToolInput.Create("Command", "whoami user; whoami admin"));

        Assert.True(decision.NeedsApproval);
        Assert.Collection(
            decision.ApprovalContext!.Candidates!,
            first => Assert.Equal(["whoami", "user"], first.VerbTokens),
            second => Assert.Equal(["whoami", "admin"], second.VerbTokens));
    }

    [Fact]
    public void One_time_keys_bind_parser_tokens_not_only_the_legacy_projection()
    {
        var first = new ApprovalCandidate("whoami", Directory: null)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(["whoami", "user"]),
        };
        var second = new ApprovalCandidate("whoami", Directory: null)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(["whoami", "admin"]),
        };

        var firstKeys = OneTimeApprovalKeys.Create([], [first], cwd: null);
        var secondKeys = OneTimeApprovalKeys.Create([], [second], cwd: null);

        Assert.NotEqual(Assert.Single(firstKeys), Assert.Single(secondKeys));
    }

    private const string ControlPlaneRoot = "/home/user/.netclaw/config";

    private static ToolAccessPolicy CreateFileWritePolicy(ToolApprovalConfig? approvalPolicy = null)
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ApprovalPolicy = approvalPolicy;
        return new ToolAccessPolicy(new NetclawPaths(),
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
    public void File_grant_tool_without_path_descriptor_fails_closed()
    {
        var registry = new ToolRegistry();
        registry.RegisterCore(
            AIFunctionFactory.Create(() => "result", "future_file_tool"),
            "file");
        var tool = Assert.IsAssignableFrom<INetclawTool>(registry.GetByName("future_file_tool"));

        var decision = CreateFileWritePolicy().AuthorizeInvocation(
            tool,
            PersonalContext(),
            ToolInput.Create("Path", "/tmp/example.txt"));

        Assert.False(decision.Allowed);
        Assert.Equal("path_access_descriptor_missing", decision.DenyReason);
        Assert.Null(decision.ApprovalContext);
    }

    [Fact]
    public void Structured_file_protection_precedes_approval_mode()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        var config = new ToolConfig();
        config.AudienceProfiles.Personal.ReadFiles.Mode = ToolFilesystemMode.None;
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [FileReadTool.ToolName] = ToolApprovalMode.Deny
            }
        };
        var protectedPaths = new ToolPathPolicy([paths.ConfigDirectory]);
        var policy = new ToolAccessPolicy(
            paths,
            config,
            Defaults(),
            new ShellCommandPolicy(),
            protectedPaths);

        var decision = policy.AuthorizeInvocation(
            new FileReadTool(config, paths, protectedPaths),
            PersonalContext(),
            ToolInput.Create("Path", paths.NetclawConfigPath));

        Assert.False(decision.Allowed);
        Assert.Equal("path_access_denied", decision.DenyReason);
        Assert.NotNull(decision.DenyMessage);
        Assert.Null(decision.ApprovalContext);
    }

    [Fact]
    public void Policy_owns_path_access_policy_for_required_paths()
    {
        var policy = new ToolAccessPolicy(new NetclawPaths(),
            new ToolConfig(),
            Defaults(),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

        Assert.NotNull(policy.SharedPathAccessPolicy);
    }

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
        return new ToolAccessPolicy(new NetclawPaths(),
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
            new NetclawPaths(),
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
        var policy = new ToolAccessPolicy(new NetclawPaths(),
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
        var policy = new ToolAccessPolicy(new NetclawPaths(),
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
        var policy = new ToolAccessPolicy(new NetclawPaths(),
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
    public void Non_interactive_shell_with_path_outside_trusted_roots_is_denied()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var outsidePath = Path.Combine(dir.Path, "outside", "secrets.txt");

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
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
    public void Non_interactive_shell_with_path_inside_trusted_root_proceeds_to_approval()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var insidePath = Path.Combine(trustedRoot, "project", "README.md");

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?>
            {
                ["command"] = TestShellEnvironment.ReadFileCommand(insidePath)
            });

        // Path authorization allows the trusted-root path, so approval runs next.
        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Non_interactive_shell_without_path_args_proceeds_to_approval()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        // "git status" has no path-like arguments
        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "git status" });

        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Interactive_shell_with_unrestricted_write_profile_reaches_approval_for_external_path()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: true);

        // The default Personal WriteFiles=All profile admits the path. The
        // approval layer still decides whether the shell call can execute.
        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "cat /etc/passwd" });

        Assert.True(decision.NeedsApproval);
    }

    [Fact]
    public void Disabled_shell_stops_before_file_protection()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var policy = CreatePolicyWithTrustedRoot(
            trustedRoot,
            ShellExecutionMode.Off,
            ToolFilesystemMode.None);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            new Dictionary<string, object?>
            {
                ["command"] = $"cat \"{Path.Combine(trustedRoot, "project", "README.md")}\""
            });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_disabled", decision.DenyReason);
    }

    [Fact]
    public void Team_file_write_does_not_require_shell_capability()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var config = new ToolConfig { ShellMode = ShellExecutionMode.Off };
        config.AudienceProfiles.Team.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [FileWriteTool.ToolName] = ToolApprovalMode.Auto
            }
        };
        var protectedPaths = new ToolPathPolicy([]);
        var policy = new ToolAccessPolicy(
            paths,
            config,
            Defaults(),
            new ShellCommandPolicy(),
            protectedPaths);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/team-file-write",
            paths.SessionsDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team
            });
        var target = Path.Combine(paths.SessionsDirectory, "result.txt");

        var decision = policy.AuthorizeInvocation(
            new FileWriteTool(config, paths, protectedPaths),
            context,
            ToolInput.Create("Path", target, "Content", "result"));

        Assert.True(decision.Allowed);
        Assert.Equal(ToolAllowReason.PolicyAuto, decision.AllowReason);
    }

    [Fact]
    public void Shell_invocation_requires_write_file_authority_before_approval()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var workingDirectory = Path.Combine(trustedRoot, "project");
        var outsidePath = Path.Combine(dir.Path, "outside", "README.md");
        var policy = CreatePolicyWithTrustedRoot(
            trustedRoot,
            ShellExecutionMode.HostAllowed,
            ToolFilesystemMode.Roots);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            new Dictionary<string, object?>
            {
                ["command"] = $"cat \"{outsidePath}\"",
                ["WorkingDirectory"] = workingDirectory
            });

        Assert.False(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal("shell_path_outside_trust_zone", decision.DenyReason);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Approval)]
    [InlineData(ToolApprovalMode.Auto)]
    public void Shell_write_none_denies_before_approval_mode(ToolApprovalMode approvalMode)
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var policy = CreatePolicyWithTrustedRoot(
            trustedRoot,
            ShellExecutionMode.HostAllowed,
            ToolFilesystemMode.None,
            approvalMode);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            new Dictionary<string, object?>
            {
                ["command"] = "git status",
                ["WorkingDirectory"] = trustedRoot
            });

        Assert.False(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal("shell_working_directory_outside_trust_zone", decision.DenyReason);
    }

    [SlopwatchSuppress("SW001", "This regression requires POSIX symbolic-link semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Directory symlink creation is privilege-gated on Windows.")]
    public void Shell_file_protection_checks_exact_path_for_link_escape()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var workingDirectory = Path.Combine(trustedRoot, "project");
        var outsideDirectory = Path.Combine(dir.Path, "outside");
        var outsideFile = Path.Combine(outsideDirectory, "README.md");
        var linkedFile = Path.Combine(workingDirectory, "linked.md");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(outsideFile, "outside");
        File.CreateSymbolicLink(linkedFile, outsideFile);
        var policy = CreatePolicyWithTrustedRoot(
            trustedRoot,
            ShellExecutionMode.HostAllowed,
            ToolFilesystemMode.Roots);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            new Dictionary<string, object?>
            {
                ["command"] = $"cat \"{linkedFile}\"",
                ["WorkingDirectory"] = workingDirectory
            });

        Assert.False(decision.Allowed);
        Assert.False(decision.NeedsApproval);
        Assert.Equal("shell_path_outside_trust_zone", decision.DenyReason);
    }

    [Fact]
    public void Unresolved_interactive_shell_path_retains_one_shot_approval()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);

        var decision = policy.AuthorizeInvocation(
            ShellTool(),
            PersonalContext(),
            ToolInput.Create("Command", "custom-command \"$TARGET_FILE\""));

        Assert.True(decision.NeedsApproval);
        Assert.Null(decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_nested_shell_path_outside_trusted_roots_is_denied()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var outsidePath = Path.Combine(dir.Path, "outside", "shadow.txt");

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var command = OperatingSystem.IsWindows()
            ? $"pwsh -NoProfile -Command \"Get-Content '{outsidePath}'\""
            : $"bash -c \"cat '{outsidePath}'\"";
        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = command });

        Assert.False(decision.Allowed);
        Assert.Equal(
            OperatingSystem.IsWindows()
                ? "shell_unresolved_trust_zone_input"
                : "shell_path_outside_trust_zone",
            decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_working_directory_outside_trusted_roots_is_denied()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var outsideDir = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(outsideDir);

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
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
    public void Non_interactive_shell_with_trusted_working_directory_and_relative_path_proceeds_to_approval()
    {
        using var dir = new DisposableTempDir();
        var trustedRoot = CreateTrustedRoot(dir.Path);
        var workingDirectory = Path.Combine(trustedRoot, "project");
        Directory.CreateDirectory(workingDirectory);

        var policy = CreatePolicyWithTrustedRoot(trustedRoot);
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
    public void Non_interactive_shell_with_path_outside_default_trusted_roots_is_denied()
    {
        // The mandatory path policy contains Netclaw's default trusted roots,
        // but an unrelated system path remains outside them.
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "cat /etc/passwd" });

        Assert.False(decision.Allowed);
        Assert.Equal("shell_path_outside_trust_zone", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_shell_with_working_directory_outside_default_trusted_roots_is_denied()
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
        Assert.Equal("shell_working_directory_outside_trust_zone", decision.DenyReason);
    }

    [Fact]
    public void Non_interactive_pathless_shell_no_longer_false_denies()
    {
        // Regression for #1244: the old empty-roots check denied EVERY
        // non-interactive Personal shell command (including path-less ones) with
        // shell_no_trust_zone_roots. The stable compatibility code retains the
        // legacy token. With the real policy a path-less
        // command now extracts no path tokens and falls through to the approval
        // gate. Path-bearing commands are covered by UnattendedPathAccessTests.
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var policy = new ToolAccessPolicy(new NetclawPaths(),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false),
            shellCommandPolicy: new ShellCommandPolicy(),
            toolPathPolicy: new ToolPathPolicy([]));
        var tool = ShellTool();
        var ctx = PersonalContext(supportsApproval: false);

        var decision = policy.AuthorizeInvocation(tool, ctx,
            new Dictionary<string, object?> { ["command"] = "git status" });

        Assert.Null(decision.DenyReason);
        Assert.True(decision.NeedsApproval);
    }

    private static string CreateTrustedRoot(string tempDir)
    {
        var root = Path.Combine(tempDir, ".netclaw", "workspaces");
        Directory.CreateDirectory(Path.Combine(root, "project"));
        return root;
    }

    private static ToolAccessPolicy CreatePolicyWithTrustedRoot(
        string trustedRoot,
        ShellExecutionMode shellMode = ShellExecutionMode.HostAllowed,
        ToolFilesystemMode writeFilesMode = ToolFilesystemMode.All,
        ToolApprovalMode approvalMode = ToolApprovalMode.Approval)
    {
        var environment = TestShellEnvironment.Current;
        var config = new ToolConfig { ShellMode = shellMode };
        config.AudienceProfiles.Personal.WriteFiles = new ToolFilesystemAccessProfile
        {
            Mode = writeFilesMode,
            Roots = [trustedRoot]
        };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = approvalMode
            }
        };

        return new ToolAccessPolicy(
            new NetclawPaths(Directory.GetParent(trustedRoot)!.FullName, trustedRoot),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false),
            shellCommandPolicy: new ShellCommandPolicy(environment),
            toolPathPolicy: new ToolPathPolicy(environment, []));
    }

    private static ToolConfig CreateShellConfig(ToolApprovalMode mode)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = mode
            }
        };
        return config;
    }

    private static EffectivePolicyDefaults Defaults()
        => new(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false);


    // ── v2 candidate-verb extraction (replaces v1 directory-root extraction) ──

    [Fact]
    public void Shell_path_command_extracts_path_aware_verb_chain_with_no_directory_roots()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var logPath = Path.Combine(ApprovalTestRoot, "logs", "crash.log");
        var args = ToolInput.Create("Command", $"cat \"{logPath}\"");

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
            Path.GetDirectoryName(logPath)!.Replace('\\', '/'),
            candidate.Directory!.Replace('\\', '/'));
    }

    [Fact]
    public void Shell_path_command_uses_fixed_labels()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var logPath = Path.Combine(ApprovalTestRoot, "logs", "app.log");
        var args = ToolInput.Create("Command", $"grep 'error' \"{logPath}\"");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        var options = decision.ApprovalContext!.Options;
        AssertFixedApprovalLabels(options);
    }

    [Fact]
    public void Shell_multi_root_command_uses_fixed_labels()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var inputPath = Path.Combine(ApprovalTestRoot, "logs", "app.log");
        var outputPath = Path.Combine(ApprovalTestRoot, "output", "report.txt");
        var args = ToolInput.Create(
            "Command",
            $"cat \"{inputPath}\" > \"{outputPath}\"");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        var options = decision.ApprovalContext!.Options;
        AssertFixedApprovalLabels(options);
    }

    [Fact]
    public void Shell_relative_path_command_extracts_verb_chain_without_directory_roots()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var root = Path.Combine(ApprovalTestRoot, "workspace");

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
        AssertFixedApprovalLabels(decision.ApprovalContext.Options);
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
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, sessionOption.Label);
        Assert.DoesNotContain(
            decision.ApprovalContext.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveAlways);
    }

    // Regression pin for issue #931 — long directory paths must not produce
    // labels that exceed `ApprovalOptionKeys.MaxLabelLength`.
    [Fact]
    public void Shell_command_with_long_directory_path_keeps_labels_within_button_caps()
    {
        var policy = CreatePolicy(ToolApprovalMode.Approval);
        var deepPath = Path.Combine(
            ApprovalTestRoot,
            "repositories",
            "example-organization",
            "test-environment",
            "services",
            "kubernetes",
            "ingress",
            "configs",
            "app.log");
        Assert.True(deepPath.Length > ApprovalOptionKeys.MaxLabelLength);

        var args = ToolInput.Create("Command", $"grep error \"{deepPath}\"");

        var decision = policy.AuthorizeInvocation(ShellTool(), PersonalContext(), args);

        Assert.True(decision.NeedsApproval);
        var options = decision.ApprovalContext!.Options;
        Assert.All(options, option => Assert.True(
            option.Label.Length <= ApprovalOptionKeys.MaxLabelLength,
            $"Option '{option.Key}' label '{option.Label}' is {option.Label.Length} chars; must stay within {ApprovalOptionKeys.MaxLabelLength}."));
        AssertFixedApprovalLabels(options);
    }

    private static void AssertFixedApprovalLabels(IReadOnlyList<ToolApprovalOption> options)
        => Assert.All(options, option => Assert.Equal(
            ApprovalOptionKeys.LabelFor(option.Key.Value),
            option.Label));

    [Fact]
    public void Narrow_shell_context_omits_reusable_options_for_incomplete_phrase_facts()
    {
        var candidate = new ApprovalCandidate("status-report", Directory: null)
        {
            Shell = ApprovalShell.Bash,
        };
        var original = new ToolApprovalContext(
            "shell_execute",
            "status-report",
            ["status-report"],
            ["status-report"],
            [],
            Cwd: "/work/repo",
            Candidates: [candidate]);

        var narrowed = ToolAccessPolicy.NarrowShellApprovalContext(
            original,
            [candidate],
            sessionOwnedDirectories: [],
            ShellPathStyle.Posix);

        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            narrowed.Options.Select(static option => option.Key.Value));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("temporary")]
    [InlineData("artifact")]
    [InlineData("worktree")]
    public void Narrow_shell_context_omits_always_here_for_named_session_storage_directory(
        string directoryKind)
    {
        using var directory = new DisposableTempDir();
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(directory.Path)));
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/storage-paths",
            storage,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });
        var selectedDirectory = directoryKind switch
        {
            "session" => storage.SessionDirectory.Value,
            "temporary" => storage.ManagedTemporary.Directory.Value,
            "artifact" => storage.ArtifactDirectory.Value,
            "worktree" => storage.WorktreeDirectory.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(directoryKind))
        };
        var candidate = new ApprovalCandidate("git status", selectedDirectory)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["git", "status"]
        };
        var original = new ToolApprovalContext(
            Netclaw.Actors.Tools.ShellTool.ToolName,
            "git status",
            ["git status"],
            ["git status"],
            [],
            Cwd: selectedDirectory,
            Candidates: [candidate]);

        var narrowed = ToolAccessPolicy.NarrowShellApprovalContext(
            original,
            [candidate],
            ToolAccessPolicy.GetSessionOwnedApprovalDirectories(context),
            ShellPathStyle.Posix);

        Assert.DoesNotContain(
            narrowed.Options,
            option => option.Key.Value == ApprovalOptionKeys.ApproveAlways);
    }

    [Theory]
    [InlineData("/", ShellPathStyle.Posix, false)]
    [InlineData("/etc", ShellPathStyle.Posix, false)]
    [InlineData("/home/user", ShellPathStyle.Posix, true)]
    [InlineData("/home//user", ShellPathStyle.Posix, false)]
    [InlineData("/etc/..", ShellPathStyle.Posix, false)]
    [InlineData("relative/repo", ShellPathStyle.Posix, false)]
    [InlineData(@"C:\", ShellPathStyle.Windows, false)]
    [InlineData(@"C:\Windows", ShellPathStyle.Windows, false)]
    [InlineData(@"C:\Users\user", ShellPathStyle.Windows, true)]
    [InlineData(@"C:\Users\\user", ShellPathStyle.Windows, false)]
    [InlineData(@"\\server\share", ShellPathStyle.Windows, false)]
    [InlineData(@"\\server\share\folder", ShellPathStyle.Windows, false)]
    [InlineData(@"\\server\share\folder\repo", ShellPathStyle.Windows, true)]
    [InlineData("\\\\ser\nver\\share\\folder\\repo", ShellPathStyle.Windows, false)]
    [InlineData("\\\\server\\sha\nre\\folder\\repo", ShellPathStyle.Windows, false)]
    [InlineData(@"\\server\\share\folder\repo", ShellPathStyle.Windows, false)]
    [InlineData(@"\\server\..\folder\repo", ShellPathStyle.Windows, false)]
    [InlineData(@"\\.\share\folder\repo", ShellPathStyle.Windows, false)]
    [InlineData(@"\\?\C:\folder\repo", ShellPathStyle.Windows, false)]
    [InlineData("\\\\server\\share\\folder\\repo\\", ShellPathStyle.Windows, false)]
    [InlineData(@"C:\work", (ShellPathStyle)999, false)]
    public void Narrow_shell_context_uses_root_relative_scope_depth(
        string cwd,
        ShellPathStyle pathStyle,
        bool offersAlwaysHere)
    {
        var candidate = new ApprovalCandidate("git status", cwd)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["git", "status"]
        };
        var original = new ToolApprovalContext(
            "shell_execute",
            "git status",
            ["git status"],
            ["git status"],
            [],
            Cwd: cwd,
            Candidates: [candidate]);

        var narrowed = ToolAccessPolicy.NarrowShellApprovalContext(
            original,
            [candidate],
            sessionOwnedDirectories: [],
            pathStyle);

        Assert.Equal(
            offersAlwaysHere,
            narrowed.Options.Any(option => option.Key.Value == ApprovalOptionKeys.ApproveAlways));
    }
}
