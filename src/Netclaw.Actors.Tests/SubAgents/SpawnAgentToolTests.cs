using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed class SpawnAgentToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public SpawnAgentToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-spawn-agent-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
        }, TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Unknown agent", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("summarizer", result, StringComparison.Ordinal);
    }
}
