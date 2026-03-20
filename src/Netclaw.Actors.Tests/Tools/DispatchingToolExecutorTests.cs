using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class DispatchingToolExecutorTests
{
    private readonly DispatchingToolExecutor _executor;
    private readonly DispatchingToolExecutor _restrictedExecutor;

    public DispatchingToolExecutorTests()
    {
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(new ToolConfig());
        _executor = new DispatchingToolExecutor(registry);

        var restrictedConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
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
            new Dictionary<string, object?> { ["Command"] = "echo routed" });

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var result = await _executor.ExecuteAsync(toolCall, context);

        Assert.Contains("routed", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Routes_file_read_missing_file()
    {
        var toolCall = new FunctionCallContent(
            "call-2", "file_read",
            new Dictionary<string, object?> { ["Path"] = "/nonexistent/file.txt" });

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", Path.GetTempPath())
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var result = await _executor.ExecuteAsync(toolCall, context);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Shell_execute_is_denied_outside_personal_context()
    {
        var toolCall = new FunctionCallContent(
            "call-deny", "shell_execute",
            new Dictionary<string, object?> { ["Command"] = "echo denied" });

        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "slack"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => _restrictedExecutor.ExecuteAsync(toolCall, context));
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
            new Dictionary<string, object?> { ["Command"] = "echo denied" });

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context));
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
            new Dictionary<string, object?> { ["Command"] = "echo denied" });

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context));
        Assert.Equal("shell_disabled", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_allowed_in_personal_context()
    {
        var toolCall = new FunctionCallContent(
            "call-allow", "shell_execute",
            new Dictionary<string, object?> { ["Command"] = "echo allowed" });

        var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", null)
        {
            Audience = TrustAudience.Personal.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "signalr"
        };

        var result = await _restrictedExecutor.ExecuteAsync(toolCall, context);
        Assert.Contains("allowed", result);
    }

    [Fact]
    public async Task File_read_is_denied_outside_session_directory_in_public_context()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-public-read-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "secret");

        try
        {
            var toolCall = new FunctionCallContent(
                "call-file-read-deny", "file_read",
                new Dictionary<string, object?> { ["Path"] = filePath });

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-public-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", sessionDir)
            {
                Audience = TrustAudience.Public.ToWireValue(),
                Boundary = SecurityPolicyDefaults.PublicBoundary,
                ChannelType = "slack"
            };

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context);
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
                new Dictionary<string, object?>
                {
                    ["Path"] = filePath,
                    ["Content"] = "blocked"
                });

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-public-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", sessionDir)
            {
                Audience = TrustAudience.Public.ToWireValue(),
                Boundary = SecurityPolicyDefaults.PublicBoundary,
                ChannelType = "slack"
            };

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context);
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
                new Dictionary<string, object?>
                {
                    ["Path"] = filePath,
                    ["Content"] = "dispatch test"
                });

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-dispatch-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = new Netclaw.Tools.ToolExecutionContext("signalr/thread-1", sessionDir)
            {
                Audience = TrustAudience.Personal.ToWireValue(),
                Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
                ChannelType = "signalr"
            };

            var result = await _executor.ExecuteAsync(toolCall, context);

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
            new Dictionary<string, object?> { ["arg"] = "value" });

        var result = await _executor.ExecuteAsync(toolCall);

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
        registry.WithFirstPartyTools(config, toolAccessPolicy: policy);

        var teamContext = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", Path.GetTempPath())
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            ChannelType = "slack"
        };

        Assert.False(policy.IsToolExposed(registry.GetByName("shell_execute")!, teamContext));
        Assert.False(policy.IsToolExposed(registry.GetByName("file_write")!, teamContext));
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
            capabilityClass: McpCapabilityClass.MemorySafe,
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

        var toolCall = new FunctionCallContent("call-mcp-deny", "memorizer/search_memories", new Dictionary<string, object?>());
        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            ChannelType = "slack"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context));
        Assert.Equal("mcp_server_not_allowed_for_audience_profile", ex.DenyReason);
    }

    [Fact]
    public async Task Mcp_tool_is_denied_when_capability_exceeds_allowed_audience()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "read_inbox"),
            "textforge",
            "read_inbox",
            capabilityClass: McpCapabilityClass.SensitiveRead,
            invoker: new RecordingMcpToolInvoker("ok")));

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Team.AllowedMcpServers.Add("textforge");

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var toolCall = new FunctionCallContent("call-mcp-capability-deny", "textforge/read_inbox", new Dictionary<string, object?>());
        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            ChannelType = "slack"
        };

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context));
        Assert.Equal("mcp_capability_denied_for_audience", ex.DenyReason);
    }
}
