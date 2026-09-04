// -----------------------------------------------------------------------
// <copyright file="ToolRegistrationExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Tests for MCP tool registration with description truncation.
/// Schema warning tests require real McpClientTool instances (MCP SDK) and are
/// covered by integration tests in Netclaw.Daemon.Tests.
/// </summary>
public class ToolRegistrationExtensionsTests
{
    [Fact]
    public Task Core_tool_names_and_schema_footprint_match_snapshot()
    {
        var registry = CreateFullFirstPartyRegistry();
        var coreTools = registry.GetCoreTools();
        var names = coreTools
            .OfType<AIFunctionDeclaration>()
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new CoreToolSnapshot(
            names,
            ModelVisibleToolFootprintCalculator.Measure(coreTools));

        return Verifier.Verify(snapshot);
    }

    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Attach_file_is_core_without_bypassing_default_audience_policy(TrustAudience audience)
    {
        var registry = CreateFullFirstPartyRegistry();
        var tool = Assert.IsType<AttachFileTool>(registry.GetByName("attach_file"));
        var policy = CreateAccessPolicy(new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed });

        Assert.True(registry.IsCoreTool(tool.Name));
        Assert.True(policy.IsToolExposed(tool, audience));
    }

    [Fact]
    public void Attach_file_core_is_hidden_by_an_audience_deny_override()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Team.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["attach_file"] = ToolApprovalMode.Deny
            }
        };
        var registry = CreateFullFirstPartyRegistry();
        var tool = Assert.IsType<AttachFileTool>(registry.GetByName("attach_file"));
        var policy = CreateAccessPolicy(config);

        Assert.True(registry.IsCoreTool(tool.Name));
        Assert.False(policy.IsToolExposed(tool, TrustAudience.Team));
    }

    [Fact]
    public void First_party_registry_composes_worktrees_without_a_custom_tool()
    {
        var registry = CreateFullFirstPartyRegistry();

        Assert.IsType<ShellTool>(registry.GetByName("shell_execute"));
        Assert.IsType<SetWorkingDirectoryTool>(registry.GetByName("set_working_directory"));
        Assert.Null(registry.GetByName("worktree_create"));
    }

    [Fact]
    public void Registration_defaults_to_deferred_and_core_replacement_is_explicit()
    {
        var registry = new ToolRegistry();
        var deferred = AIFunctionFactory.Create(() => "ok", "specialty_tool", "Specialty action");
        var core = AIFunctionFactory.Create(() => "ok", "core_tool", "Core action");

        registry.Register(deferred, "builtin");
        registry.RegisterCore(core, "builtin");

        Assert.Equal(["core_tool"], GetFunctionNames(registry.GetCoreTools()));

        registry.Replace(new FakeTool("core_tool"));
        Assert.Empty(registry.GetCoreTools());

        registry.ReplaceCore(new FakeTool("core_tool"));
        Assert.Equal(["core_tool"], GetFunctionNames(registry.GetCoreTools()));
    }

    [Fact]
    public void First_party_registration_rejects_missing_access_policy_before_mutation()
    {
        var registry = new ToolRegistry();
        var config = new ToolConfig();

        Assert.Throws<ArgumentNullException>(() => registry.WithFirstPartyTools(null!));
        Assert.Empty(registry.GetAllRegistrations());
    }

    [Fact]
    public async Task First_party_structured_tools_reuse_the_access_policy_custom_root()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(Path.Combine(directory.Path, "custom-home"));
        var config = new ToolConfig();
        var pathPolicy = new ToolPathPolicy([]);
        var commandPolicy = new ShellCommandPolicy();
        var policy = new ToolAccessPolicy(
            paths,
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(policy);
        var tool = Assert.IsType<FileWriteTool>(registry.GetByName(FileWriteTool.ToolName));
        var targetPath = Path.Combine(paths.SessionsDirectory, "other-session", "result.txt");
        var currentSession = Path.Combine(directory.Path, "current-session");
        Directory.CreateDirectory(currentSession);
        var context = TestToolExecutionContext.CreateBound(
            "signalr/current-session",
            currentSession,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr"
            });
        var arguments = ToolInput.Create("Path", targetPath, "Content", "shared policy");

        var preflight = policy.AuthorizeInvocation(tool, context, arguments);
        var result = await tool.ExecuteAsync(arguments, context, TestContext.Current.CancellationToken);

        Assert.True(preflight.Allowed);
        Assert.Contains("Successfully wrote", result, StringComparison.Ordinal);
        Assert.Equal(
            "shared policy",
            await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Skill_registration_preserves_file_read_logging()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        var skillDirectory = Path.Combine(paths.SkillsDirectory, "tracked-skill");
        var skillFile = Path.Combine(skillDirectory, "SKILL.md");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            skillFile,
            "---\nname: tracked-skill\ndescription: tracked\n---\n\n# Tracked",
            TestContext.Current.CancellationToken);

        var skillRegistry = new SkillRegistry();
        var scan = SkillScanner.Scan(paths.SkillsDirectory);
        skillRegistry.ReplaceAll(scan.AcceptedSkills, scan.Issues);
        var policy = CreateAccessPolicy(new ToolConfig(), paths);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(policy);
        var indexPublisher = new SkillIndexPublisher(
            skillRegistry,
            new SkillIndexContextLayer(),
            static (_, _) => true);
        var refresher = new SkillInventoryRefresher(
            paths,
            new SkillFeedsConfig(),
            [],
            skillRegistry,
            indexPublisher);
        var logger = new RecordingLogger<FileReadTool>();
        registry.WithSkillTools(
            policy,
            skillRegistry,
            paths,
            new NoOpSkillContentScanner(),
            new UnavailablePromptLoader(),
            refresher,
            logger);
        var context = TestToolExecutionContext.CreateBound(
            "slack/thread-1",
            Path.Combine(paths.SessionsDirectory, "thread-1"),
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                ChannelType = "slack"
            });
        var tool = Assert.IsType<FileReadTool>(registry.GetByName(FileReadTool.ToolName));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Path", skillFile),
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("# Tracked", result, StringComparison.Ordinal);
        Assert.Contains(
            logger.Messages,
            static message => message.Contains(
                "turn_skill_loaded skill=tracked-skill method=file_read",
                StringComparison.Ordinal));
    }

    [Fact]
    public void McpToolAdapter_Truncated_SanitizedAIFunction_UsesClampedDescription()
    {
        var longDescription = new string('y', 10000);
        var fakeTool = AIFunctionFactory.Create(() => "result", "big_tool", longDescription);
        var adapter = new McpToolAdapter(fakeTool, "notion", "big_tool", maxDescriptionChars: 2048);

        // The AITool exposed to the LLM should have the truncated description
        var aiTool = adapter.ToAITool();
        var aiFunc = Assert.IsAssignableFrom<AIFunction>(aiTool);
        Assert.Equal(2048 + " [truncated]".Length, aiFunc.Description.Length);
        Assert.EndsWith(" [truncated]", aiFunc.Description);

        // Verify adapter.Description matches what the LLM sees
        Assert.Equal(adapter.Description, aiFunc.Description);
    }

    private static ToolRegistry CreateFullFirstPartyRegistry()
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-core-tool-contract"));
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var policy = CreateAccessPolicy(config, paths);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(policy);

        var skillRegistry = new SkillRegistry();
        var scanner = new NoOpSkillContentScanner();
        var indexPublisher = new SkillIndexPublisher(
            skillRegistry,
            new SkillIndexContextLayer(),
            static (_, _) => true);
        var refresher = new SkillInventoryRefresher(
            paths,
            new SkillFeedsConfig(),
            [],
            skillRegistry,
            indexPublisher);
        registry.WithSkillTools(
            policy,
            skillRegistry,
            paths,
            scanner,
            new UnavailablePromptLoader(),
            refresher,
            new RecordingLogger<FileReadTool>());

        return registry;
    }

    private static ToolAccessPolicy CreateAccessPolicy(ToolConfig config, NetclawPaths? paths = null) =>
        new(
            paths ?? new NetclawPaths(),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

    private static string[] GetFunctionNames(IReadOnlyList<AITool> tools) =>
        tools
            .OfType<AIFunctionDeclaration>()
            .Select(static tool => tool.Name)
            .ToArray();

    private sealed record CoreToolSnapshot(
        IReadOnlyList<string> Names,
        ModelVisibleToolFootprint Footprint);

    private sealed class FakeTool(string name) : INetclawTool
    {
        private readonly AIFunction _function = AIFunctionFactory.Create(() => "ok", name, "Replacement");

        public string Name { get; } = name;
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical(name);
        public string Description => "Replacement";
        public string GrantCategory => "builtin";
        public System.Text.Json.JsonElement ParameterSchema => default;
        public AITool ToAITool() => _function;

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            ToolInvocationContext context,
            CancellationToken ct = default) => Task.FromResult("ok");
    }

    private sealed class UnavailablePromptLoader : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(
            McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments,
            ToolInvocationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(McpPromptSkillLoadResult.Failed("unavailable"));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class EmptyScope : IDisposable
        {
            public static EmptyScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
