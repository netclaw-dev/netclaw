// -----------------------------------------------------------------------
// <copyright file="SpawnAgentToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.SubAgents;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed class SpawnAgentToolTests : IDisposable
{
    private static readonly ToolExecutionContext PersonalCtx =
        TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal });

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
        var publicCtx = TestToolExecutionContext.CreateUnbound();

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
        // Audience is non-nullable; Public is the minimum-privilege audience, equivalent to the old null/unset default.
        var badCtx = TestToolExecutionContext.CreateUnbound();

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

    [Fact]
    public async Task ExecuteAsync_when_agent_file_was_rejected_names_the_file_and_reason()
    {
        var path = Path.Combine(_paths.AgentsDirectory, "bad-role.md");
        File.WriteAllText(path, """
            ---
            name: bad-role
            description: Profile with an invalid model role
            modelRole: Bogus
            ---

            You are bad.
            """);
        var loader = new FileSubAgentDefinitionLoader(_paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
        var tool = new SpawnAgentTool(new SubAgentDefinitionRegistry(), spawner: null!, _paths, loader: loader);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "bad-role",
            ["task"] = "summarize docs"
        }, PersonalCtx, TestContext.Current.CancellationToken);

        Assert.Contains(path, result, StringComparison.Ordinal);
        Assert.Contains("modelRole", result, StringComparison.Ordinal);
        Assert.Contains("Bogus", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_when_agent_name_has_no_file_keeps_unknown_agent_message()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "summarizer.md"), """
            ---
            name: summarizer
            description: Summarize content
            ---

            You are a summarizer.
            """);
        var badPath = Path.Combine(_paths.AgentsDirectory, "bad-role.md");
        File.WriteAllText(badPath, """
            ---
            name: bad-role
            description: Profile with an invalid model role
            modelRole: Bogus
            ---

            You are bad.
            """);
        var loader = new FileSubAgentDefinitionLoader(_paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
        var tool = new SpawnAgentTool(new SubAgentDefinitionRegistry(), spawner: null!, _paths, loader: loader);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["agent"] = "ghost",
            ["task"] = "summarize docs"
        }, PersonalCtx, TestContext.Current.CancellationToken);

        Assert.Equal("Error: Unknown agent 'ghost'. Available agents: summarizer", result);
        Assert.DoesNotContain(badPath, result, StringComparison.Ordinal);
    }
}
