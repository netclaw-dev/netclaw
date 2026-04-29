// -----------------------------------------------------------------------
// <copyright file="DispatchingToolExecutorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class DispatchingToolExecutorTests
{
    private readonly DispatchingToolExecutor _executor;
    private readonly DispatchingToolExecutor _restrictedExecutor;

    public DispatchingToolExecutorTests()
    {
        var baseConfig = new ToolConfig();
        baseConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Auto
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(baseConfig);
        _executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                baseConfig,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var restrictedConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        restrictedConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Auto
            }
        };
        restrictedConfig.AudienceProfiles.Team.AllowedTools = ["file_read", "attach_file", "shell_execute"];
        restrictedConfig.AudienceProfiles.Public.AllowedTools = ["file_read", "file_write", "attach_file"];
        var restrictedRegistry = new ToolRegistry();
        restrictedRegistry.WithFirstPartyTools(restrictedConfig);
        _restrictedExecutor = new DispatchingToolExecutor(
            restrictedRegistry,
            new ToolAccessPolicy(
                restrictedConfig,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));
    }

    [Fact]
    public async Task Routes_shell_execute()
    {
        var toolCall = new FunctionCallContent(
            "call-1", "shell_execute",
            ToolInput.Create("Command", "echo routed"));

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var result = await _executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        Assert.Contains("routed", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Routes_file_read_missing_file()
    {
        var toolCall = new FunctionCallContent(
            "call-2", "file_read",
            ToolInput.Create("Path", "/nonexistent/file.txt"));

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", Path.GetTempPath())
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var result = await _executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Shell_execute_is_denied_outside_personal_context()
    {
        var toolCall = new FunctionCallContent(
            "call-deny", "shell_execute",
            ToolInput.Create("Command", "echo denied"));

        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "slack"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("shell_requires_personal_context", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_denied_when_missing_from_personal_audience_profile()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ToolsMode = ToolProfileMode.Allowlist;
        config.AudienceProfiles.Personal.AllowedTools = ["file_read", "file_write", "attach_file"];

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var toolCall = new FunctionCallContent(
            "call-shell-profile-deny", "shell_execute",
            ToolInput.Create("Command", "echo denied"));

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("tool_not_allowed_for_audience_profile", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_denied_when_shell_mode_is_off_even_in_personal_context()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.Off };
        config.AudienceProfiles.Personal.ToolsMode = ToolProfileMode.Allowlist;
        config.AudienceProfiles.Personal.AllowedTools.Add("shell_execute");

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.Off,
                    UsedStrictFallback: false)));

        var toolCall = new FunctionCallContent(
            "call-shell-off", "shell_execute",
            ToolInput.Create("Command", "echo denied"));

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("shell_disabled", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_allowed_in_personal_context()
    {
        var toolCall = new FunctionCallContent(
            "call-allow", "shell_execute",
            ToolInput.Create("Command", "echo allowed"));

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
        Assert.Contains("allowed", result);
    }

    [Fact]
    public async Task File_read_is_denied_outside_session_directory_in_public_context()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-public-read-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "secret", TestContext.Current.CancellationToken);

        try
        {
            var toolCall = new FunctionCallContent(
                "call-file-read-deny", "file_read",
                ToolInput.Create("Path", filePath));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-public-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", sessionDir)
            {
                Audience = TrustAudience.Public.ToWireValue(),
                Boundary = SecurityPolicyDefaults.PublicBoundary,
                ChannelType = "slack"
            };

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Public trust context", result);
            Assert.Contains("session directory", result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task File_write_is_denied_outside_session_directory_in_public_context()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-public-write-{Guid.NewGuid():N}.txt");

        try
        {
            var toolCall = new FunctionCallContent(
                "call-file-write-deny", "file_write",
                ToolInput.Create("Path", filePath, "Content", "blocked"));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-public-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", sessionDir)
            {
                Audience = TrustAudience.Public.ToWireValue(),
                Boundary = SecurityPolicyDefaults.PublicBoundary,
                ChannelType = "slack"
            };

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Public trust context", result);
            Assert.Contains("session directory", result);
            Assert.False(File.Exists(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Routes_file_write()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-dispatch-{Guid.NewGuid():N}.txt");
        try
        {
            var toolCall = new FunctionCallContent(
                "call-3", "file_write",
                ToolInput.Create("Path", filePath, "Content", "dispatch test"));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-dispatch-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", sessionDir)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr"
            };

            var result = await _executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

            Assert.Contains("Successfully wrote", result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Unknown_tool_returns_error_string()
    {
        var toolCall = new FunctionCallContent(
            "call-4", "unknown_tool",
            ToolInput.Create("arg", "value"));

        var result = await _executor.ExecuteAsync(toolCall, null, TestContext.Current.CancellationToken);

        Assert.Equal("Unknown tool: unknown_tool", result);
    }

    [Fact]
    public void Team_profile_hides_shell_and_write_tools()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Team.AllowedTools = ["file_read", "attach_file"];

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));

        var registry = new ToolRegistry();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-webhook-tools-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        registry.WithFirstPartyTools(config, toolAccessPolicy: policy, paths: paths, webhookRouteStore: new WebhookRouteStore(paths));

        var teamContext = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", Path.GetTempPath())
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            ChannelType = "slack"
        };

        Assert.False(policy.IsToolExposed(registry.GetByName("shell_execute")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("file_write")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("set_webhook")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("list_webhooks")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("delete_webhook")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_read")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("attach_file")!, teamContext));
    }

    [Fact]
    public async Task Mcp_tool_is_denied_when_server_not_allowed_for_audience()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "search_memories"),
            "memorizer",
            "search_memories",
            invoker: new RecordingMcpToolInvoker("ok")));

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var toolCall = new FunctionCallContent("call-mcp-deny", "memorizer/search_memories", ToolInput.Empty());
        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            ChannelType = "slack"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("mcp_server_not_allowed_for_audience_profile", ex.DenyReason);
    }

    [Fact]
    public async Task One_time_approval_allows_immediate_retry_only()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);

        var system = ActorSystem.Create($"tool-approval-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false)),
                approvalService);

            var toolCall = new FunctionCallContent(
                "call-approve-once",
                "shell_execute",
                ToolInput.Create("Command", "echo once"));

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = toolCall.Name;
            context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.UnapprovedPatterns);

            var retryResult = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("once", retryResult);

            context.OneTimeApprovedToolName = null;
            context.SetOneTimeApprovedPatterns([]);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task One_time_approval_bypasses_policy_for_matching_shell_patterns()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var toolCall = new FunctionCallContent(
            "call-approve-once-bypass",
            "shell_execute",
            ToolInput.Create("Command", "echo bypass"));

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr",
            SupportsInteractiveApproval = true
        };

        var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
            executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

        context.OneTimeApprovedToolName = toolCall.Name;
        context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.UnapprovedPatterns);

        var retryResult = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        Assert.Contains("bypass", retryResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_time_approval_bypasses_policy_for_path_aware_file_patterns()
    {
        var controlPlaneRoot = Path.Combine(Path.GetTempPath(), $"netclaw-control-plane-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(controlPlaneRoot, "netclaw.json");
        var secondPath = Path.Combine(controlPlaneRoot, "devices.json");
        Directory.CreateDirectory(controlPlaneRoot);

        try
        {
            var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
            config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    ["shell_execute"] = ToolApprovalMode.Approval
                }
            };

            var registry = new ToolRegistry();
            registry.WithFirstPartyTools(config);

            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    fileApprovalMatcher: new FilePathApprovalMatcher(controlPlaneRoot)));

            var toolCall = new FunctionCallContent(
                "call-file-approve-once-bypass",
                "file_write",
                ToolInput.Create("Path", targetPath, "Content", "approved once"));

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = toolCall.Name;
            context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.UnapprovedPatterns);

            var retryResult = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Successfully wrote", retryResult, StringComparison.Ordinal);
            Assert.True(File.Exists(targetPath));

            var secondCall = new FunctionCallContent(
                "call-file-approve-once-bypass-second",
                "file_write",
                ToolInput.Create("Path", secondPath, "Content", "different path"));

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(secondCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = null;
            context.SetOneTimeApprovedPatterns([]);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(controlPlaneRoot))
                Directory.Delete(controlPlaneRoot, recursive: true);
        }
    }

    [Fact]
    public async Task One_time_approval_uses_filtered_unapproved_patterns_on_retry()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);

        var system = ActorSystem.Create($"tool-approval-filtered-once-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false)),
                approvalService);

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-filtered", null)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            await approvalService.RecordApprovalAsync(
                "signalr/thread-filtered",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                ["pwd"],
                persistent: false,
                TestContext.Current.CancellationToken);

            var call = new FunctionCallContent(
                "call-filtered-once",
                "shell_execute",
                ToolInput.Create("Command", "pwd && ls"));

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken));

            Assert.DoesNotContain("pwd", firstAttempt.ApprovalContext.UnapprovedPatterns);
            Assert.Contains("ls", firstAttempt.ApprovalContext.UnapprovedPatterns);

            context.OneTimeApprovedToolName = call.Name;
            context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.UnapprovedPatterns);

            var retryResult = await executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken);
            Assert.Contains("Exit code: 0", retryResult, StringComparison.Ordinal);

            context.OneTimeApprovedToolName = null;
            context.SetOneTimeApprovedPatterns([]);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Session_approval_allows_same_session_but_not_different_session()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config);

        var system = ActorSystem.Create($"tool-approval-session-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false)),
                approvalService);

            var toolCall = new FunctionCallContent(
                "call-session-approve",
                "shell_execute",
                ToolInput.Create("Command", "echo session"));

            var firstContext = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var secondContext = new Netclaw.Tools.ToolExecutionContext("signalr/thread-2", null)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, firstContext, TestContext.Current.CancellationToken));

            await approvalService.RecordApprovalAsync(
                "signalr/thread-1",
                TrustAudience.Personal,
                new ToolName(toolCall.Name),
                firstAttempt.ApprovalContext.UnapprovedPatterns,
                persistent: false,
                TestContext.Current.CancellationToken);

            var sameSessionResult = await executor.ExecuteAsync(toolCall, firstContext, TestContext.Current.CancellationToken);
            Assert.Contains("session", sameSessionResult);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, secondContext, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    private sealed class StubRequiredActor : IRequiredActor<ToolApprovalActorKey>
    {
        private readonly IActorRef _actor;

        public StubRequiredActor(IActorRef actor)
        {
            _actor = actor;
        }

        public IActorRef ActorRef => _actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_actor);
    }

}
