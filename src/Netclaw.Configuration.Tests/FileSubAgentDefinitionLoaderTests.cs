using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Netclaw.Configuration.Tests;

public class FileSubAgentDefinitionLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FileSubAgentDefinitionLoader _loader;

    public FileSubAgentDefinitionLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
        _loader = new FileSubAgentDefinitionLoader(_paths, NullLogger<FileSubAgentDefinitionLoader>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadAll_returns_empty_when_no_files()
    {
        var results = _loader.LoadAll();
        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_loads_inline_system_prompt()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "test-agent",
              "description": "A test agent",
              "systemPrompt": "You are a test agent.",
              "tools": ["web_search"],
              "timeoutSeconds": 30
            }
            """);

        var results = _loader.LoadAll();
        Assert.Single(results);
        Assert.Equal("test-agent", results[0].Name);
        Assert.Equal("You are a test agent.", results[0].EffectiveSystemPrompt);
    }

    [Fact]
    public void LoadAll_loads_system_prompt_from_file()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "test-agent",
              "description": "A test agent",
              "systemPromptFile": "test.md",
              "tools": ["web_search"]
            }
            """);
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.md"),
            "You are a test agent from a file.");

        var results = _loader.LoadAll();
        Assert.Single(results);
        Assert.Equal("You are a test agent from a file.", results[0].EffectiveSystemPrompt);
    }

    [Fact]
    public void LoadAll_skips_missing_prompt_file()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "test-agent",
              "description": "A test agent",
              "systemPromptFile": "nonexistent.md",
              "tools": ["web_search"]
            }
            """);

        var results = _loader.LoadAll();
        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_agent_with_no_tools()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "test-agent",
              "description": "A test agent",
              "systemPrompt": "You are a test agent.",
              "tools": []
            }
            """);

        var results = _loader.LoadAll();
        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_agent_with_no_name()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "description": "A test agent",
              "systemPrompt": "You are a test agent.",
              "tools": ["web_search"]
            }
            """);

        var results = _loader.LoadAll();
        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_invalid_json()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "bad.json"), "not json at all");

        var results = _loader.LoadAll();
        Assert.Empty(results);
    }

    [Fact]
    public void ToProfile_converts_correctly()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "research-assistant",
              "description": "Deep research",
              "systemPrompt": "You are a researcher.",
              "tools": ["web_search", "web_fetch"],
              "modelRole": "Main",
              "timeoutSeconds": 120
            }
            """);

        var results = _loader.LoadAll();
        var profile = results[0].ToProfile();

        Assert.Equal("research-assistant", profile.Name);
        Assert.Equal("Deep research", profile.Description);
        Assert.Equal("You are a researcher.", profile.SystemPrompt);
        Assert.Equal(["web_search", "web_fetch"], profile.ToolNames);
        Assert.Equal(ModelRole.Main, profile.ModelRole);
        Assert.Equal(120, profile.TimeoutSeconds);
        Assert.Equal(SubAgentVisibility.UserFacing, profile.Visibility);
    }

    [Fact]
    public void ToProfile_defaults_model_role_to_compaction()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "test",
              "description": "Test",
              "systemPrompt": "Test.",
              "tools": ["web_search"]
            }
            """);

        var results = _loader.LoadAll();
        var profile = results[0].ToProfile();

        Assert.Equal(ModelRole.Compaction, profile.ModelRole);
        Assert.Equal(60, profile.TimeoutSeconds);
    }

    [Fact]
    public void LoadAll_skips_prompt_path_outside_agents_directory()
    {
        var escapedPath = Path.Combine("..", "outside.md");
        File.WriteAllText(Path.Combine(_tempDir, "outside.md"), "secret");
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), $$"""
            {
              "name": "test-agent",
              "description": "A test agent",
              "systemPromptFile": "{{escapedPath}}",
              "tools": ["web_search"]
            }
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_user_facing_agent_with_disallowed_tools()
    {
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "test.json"), """
            {
              "name": "test-agent",
              "description": "A test agent",
              "systemPrompt": "You are a test agent.",
              "tools": ["web_search", "file_write", "shell_execute"]
            }
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }
}
