// -----------------------------------------------------------------------
// <copyright file="ToolRegistrationExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
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

        Assert.Throws<ArgumentNullException>(() => registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            new ToolPathPolicy([]),
            new ShellCommandPolicy()));
        Assert.Empty(registry.GetAllRegistrations());
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
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            paths,
            new ToolPathPolicy([]),
            new ShellCommandPolicy(),
            toolAccessPolicy: policy);

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
            skillRegistry,
            paths,
            scanner,
            new UnavailablePromptLoader(),
            refresher);

        return registry;
    }

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
}
