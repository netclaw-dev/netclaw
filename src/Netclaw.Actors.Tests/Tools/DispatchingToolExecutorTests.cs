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
        restrictedConfig.AudienceProfiles.Team.AllowedTools = ["file_read", "file_list", "file_write", "file_edit", "attach_file", "shell_execute"];
        restrictedConfig.AudienceProfiles.Public.AllowedTools = ["file_read", "file_list", "attach_file"];
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
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
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
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
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
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.TrustedInstance,
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
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
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
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
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
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
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
                Audience = TrustAudience.Public,
                Boundary = TrustBoundary.Public,
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
    public async Task File_write_is_denied_outside_session_directory_in_team_context()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-team-write-{Guid.NewGuid():N}.txt");

        try
        {
            var toolCall = new FunctionCallContent(
                "call-file-write-deny", "file_write",
                ToolInput.Create("Path", filePath, "Content", "blocked"));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-team-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", sessionDir)
            {
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                ChannelType = "slack"
            };

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Team trust context", result);
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
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
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
    public void Team_profile_exposes_file_tools_and_hides_shell_and_webhooks()
    {
        // Default Team profile (no explicit AllowedTools override).
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };

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
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = "slack"
        };

        Assert.True(policy.IsToolExposed(registry.GetByName("file_read")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_list")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_write")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_edit")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("attach_file")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("set_working_directory")!, teamContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("web_fetch")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("shell_execute")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("set_webhook")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("list_webhooks")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("delete_webhook")!, teamContext));
    }

    [Fact]
    public void Public_profile_exposes_read_tools_and_hides_mutation_tools()
    {
        // Default Public profile — least-trusted: read, enumerate, attach only.
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));

        var registry = new ToolRegistry();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-public-tools-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        registry.WithFirstPartyTools(config, toolAccessPolicy: policy, paths: paths, webhookRouteStore: new WebhookRouteStore(paths));

        var publicContext = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", Path.GetTempPath())
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            ChannelType = "slack"
        };

        Assert.True(policy.IsToolExposed(registry.GetByName("file_read")!, publicContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_list")!, publicContext));
        Assert.True(policy.IsToolExposed(registry.GetByName("attach_file")!, publicContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("file_write")!, publicContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("file_edit")!, publicContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("shell_execute")!, publicContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("set_working_directory")!, publicContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("web_fetch")!, publicContext));
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
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
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
                // Use a non-side-effect verb (echo/printf/:/true/false
                // auto-allow at the matcher level under v2.1) so the
                // approval flow this test exercises actually triggers.
                ToolInput.Create("Command", "git status"));

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = toolCall.Name;
            context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.Patterns);

            // The one-time-approval bypass should let the call succeed.
            // Output text varies by test environment (git status); meaningful
            // assertion is that no ToolApprovalRequiredException is thrown.
            _ = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

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
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr",
            SupportsInteractiveApproval = true
        };

        var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
            executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

        context.OneTimeApprovedToolName = toolCall.Name;
        context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.Patterns);

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
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = toolCall.Name;
            context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.Patterns);

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
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            await approvalService.RecordApprovalAsync(
                "signalr/thread-filtered",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                ["pwd"],
                persistent: false,
                cwd: null,
                TestContext.Current.CancellationToken);

            var call = new FunctionCallContent(
                "call-filtered-once",
                "shell_execute",
                ToolInput.Create("Command", "pwd && ls"));

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken));

            Assert.Contains("pwd", firstAttempt.ApprovalContext.Patterns);
            Assert.Contains("ls", firstAttempt.ApprovalContext.Patterns);

            context.OneTimeApprovedToolName = call.Name;
            context.SetOneTimeApprovedPatterns(firstAttempt.ApprovalContext.Patterns);

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
    public async Task Persistent_approval_hit_records_audit_context_without_prompting()
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

        var tempFile = Path.GetTempFileName();
        var system = ActorSystem.Create($"tool-approval-audit-{Guid.NewGuid():N}");
        try
        {
            var store = new ToolApprovalStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                new ApprovalEntry("git status") { Directory = null });

            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(store), "tool-approval");
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

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-audit", null)
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var call = new FunctionCallContent(
                "call-audit",
                "shell_execute",
                ToolInput.Create("Command", "git status"));

            await executor.AuthorizeAsync(call, context, TestContext.Current.CancellationToken);

            Assert.Equal("PreviouslyApproved", context.AppliedApprovalDecision);
            Assert.Equal("git status [persistent: git status anywhere]", context.AppliedApprovalPattern);
        }
        finally
        {
            File.Delete(tempFile);
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
                // Non-side-effect verb so the approval flow under test
                // actually triggers (see same change above for
                // One_time_approval_allows_immediate_retry_only).
                ToolInput.Create("Command", "git status"));

            var firstContext = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var secondContext = new Netclaw.Tools.ToolExecutionContext("signalr/thread-2", null)
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, firstContext, TestContext.Current.CancellationToken));

            await approvalService.RecordApprovalAsync(
                "signalr/thread-1",
                TrustAudience.Personal,
                new ToolName(toolCall.Name),
                firstAttempt.ApprovalContext.CandidateVerbs,
                persistent: false,
                cwd: null,
                TestContext.Current.CancellationToken);

            // Approved in firstContext's session — call should succeed.
            // The output text varies by test environment (git status may
            // error if not in a repo), but the meaningful assertion is
            // that no ToolApprovalRequiredException was thrown.
            _ = await executor.ExecuteAsync(toolCall, firstContext, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, secondContext, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    // Regression for #1133: PR #1134 introduced an Anthropic-safe sanitized
    // alias (`server__tool`) for MCP tool names. The LLM emits tool_use with
    // the sanitized form, but the policy/session actor record approval under
    // the canonical `server/tool`. Looking up by the sanitized form on retry
    // miscounted every approved grant as unapproved and threw
    // ToolApprovalRequiredException on every post-approval call — surfaced
    // in production as "I encountered an error executing a tool" loops on
    // Notion writes.
    [Fact]
    public async Task Mcp_session_approval_recorded_under_canonical_name_authorizes_sanitized_alias_retry()
    {
        const string serverName = "notion";
        const string bareToolName = "notion-create-pages";
        const string canonicalName = $"{serverName}/{bareToolName}";
        const string sanitizedAlias = $"{serverName}__{bareToolName}";

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            // Override keyed on the canonical name — same form the policy
            // uses when it builds the approval gate for MCP tools.
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [canonicalName] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", bareToolName),
            serverName,
            bareToolName,
            invoker: new RecordingMcpToolInvoker("ok")));

        // Sanity check: the adapter exposes the sanitized alias to the LLM
        // while keeping the canonical name as its primary identity. If this
        // ever changes, the rest of the test loses its meaning.
        var adapter = (McpToolAdapter)registry.GetByName(canonicalName)!;
        Assert.Equal(canonicalName, adapter.Name);
        Assert.Equal(sanitizedAlias, adapter.SanitizedName);

        var system = ActorSystem.Create($"tool-approval-mcp-{Guid.NewGuid():N}");
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

            // The LLM emits tool_use with the sanitized alias — mirror that
            // here. The registry's two-form lookup (introduced in PR #1134)
            // resolves it back to the same adapter.
            var toolCall = new FunctionCallContent(
                "call-mcp-approve-session",
                sanitizedAlias,
                ToolInput.Empty());

            var context = new Netclaw.Tools.ToolExecutionContext("slack/D0/1779", null)
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "slack",
                SupportsInteractiveApproval = true
            };

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            // The approval context — and the slack prompt the user sees —
            // carry the canonical name, not the sanitized alias.
            Assert.Equal(canonicalName, firstAttempt.ApprovalContext.ToolName);

            // Simulate LlmSessionActor.PersistApprovalCandidatesAsync on an
            // ApprovedSession click: the grant is recorded under the
            // canonical name (pending.ToolName), with the canonical
            // candidate verb extracted by DefaultApprovalMatcher.
            await approvalService.RecordApprovalAsync(
                "slack/D0/1779",
                TrustAudience.Personal,
                new ToolName(canonicalName),
                firstAttempt.ApprovalContext.CandidateVerbs,
                persistent: false,
                cwd: null,
                TestContext.Current.CancellationToken);

            // Retry — still under the sanitized alias the LLM uses. Pre-fix
            // this re-threw ToolApprovalRequiredException because the
            // executor looked up the grant by toolCall.Name (sanitized)
            // while it had been stored under tool.Name (canonical).
            _ = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

            // Same call dispatched by the canonical name must also resolve
            // — the registry accepts both forms, so the gate should
            // authorize either way.
            var canonicalToolCall = new FunctionCallContent(
                "call-mcp-approve-session-canonical",
                canonicalName,
                ToolInput.Empty());
            _ = await executor.ExecuteAsync(canonicalToolCall, context, TestContext.Current.CancellationToken);
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
