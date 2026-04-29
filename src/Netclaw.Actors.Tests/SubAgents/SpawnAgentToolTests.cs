// -----------------------------------------------------------------------
// <copyright file="SpawnAgentToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed class SpawnAgentToolTests : IDisposable
{
    private static readonly ToolExecutionContext PersonalCtx =
        new(null, null) { Audience = TrustAudience.Personal.ToWireValue() };

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SpawnAgentToolTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsGenericDenialForPublicAudience()
    {
        var registry = new SubAgentDefinitionRegistry();
        registry.Register(new SubAgentProfile
        {
            Name = "secret-agent",
            Description = "Secret agent",
            SystemPrompt = "You are secret.",
            ToolNames = ["file_read"],
            Visibility = SubAgentVisibility.UserFacing
        });
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths);
        var publicCtx = new ToolExecutionContext(null, null) { Audience = TrustAudience.Public.ToWireValue() };

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "secret-agent",
            ["task"] = "summarize docs"
        }, publicCtx, TestContext.Current.CancellationToken);

        Assert.Equal("Error: This tool is not available.", result);
        // Must NOT leak agent names
        Assert.DoesNotContain("secret-agent", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsGenericDenialWhenSubAgentDisabled()
    {
        var registry = new SubAgentDefinitionRegistry();
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths,
            subAgentConfig: new SubAgentConfig { Enabled = false });

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "research-assistant",
            ["task"] = "summarize docs"
        }, PersonalCtx, TestContext.Current.CancellationToken);

        Assert.Equal("Error: This tool is not available.", result);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToPublicWhenAudienceUnparseable()
    {
        var registry = new SubAgentDefinitionRegistry();
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths);
        var badCtx = new ToolExecutionContext(null, null) { Audience = "superadmin" };

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "research-assistant",
            ["task"] = "summarize docs"
        }, badCtx, TestContext.Current.CancellationToken);

        Assert.Equal("Error: This tool is not available.", result);
    }

    [Fact]
    public async Task ExecuteAsync_when_no_user_facing_subagents_returns_actionable_error()
    {
        var registry = new SubAgentDefinitionRegistry();
        var tool = new SpawnAgentTool(registry, spawner: null!, _paths);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "research-assistant",
            ["task"] = "summarize docs"
        }, PersonalCtx, TestContext.Current.CancellationToken);

        Assert.Contains("No subagents are available", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("research-assistant", result, StringComparison.Ordinal);
        Assert.Contains(_paths.AgentsDirectory, result, StringComparison.Ordinal);
        Assert.Contains("metadata.subagent", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_when_agent_is_unknown_lists_available_user_facing_agents()
    {
        var registry = new SubAgentDefinitionRegistry();
        registry.Register(new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["file_read"],
            Visibility = SubAgentVisibility.UserFacing
        });

        var tool = new SpawnAgentTool(registry, spawner: null!, _paths);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "research-assistant",
            ["task"] = "summarize docs"
        }, PersonalCtx, TestContext.Current.CancellationToken);

        Assert.Contains("Unknown agent", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("summarizer", result, StringComparison.Ordinal);
    }
}
