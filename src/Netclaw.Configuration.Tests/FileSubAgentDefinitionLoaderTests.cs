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

    private string WriteAgent(string fileName, string content)
    {
        var path = Path.Combine(_paths.AgentsDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadAll_returns_empty_when_no_files()
    {
        var results = _loader.LoadAll();
        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_parses_full_frontmatter_and_body()
    {
        WriteAgent("research-assistant.md", """
            ---
            name: research-assistant
            description: Deep research
            tools: [web_search, web_fetch]
            modelRole: Main
            timeoutSeconds: 120
            visibility: user-facing
            emitStructuredFindings: true
            ---

            You are a researcher.
            Follow sources carefully.
            """);

        var results = _loader.LoadAll();

        Assert.Single(results);
        var profile = results[0];
        Assert.Equal("research-assistant", profile.Name);
        Assert.Equal("Deep research", profile.Description);
        Assert.Contains("You are a researcher.", profile.SystemPrompt);
        Assert.Contains("Follow sources carefully.", profile.SystemPrompt);
        Assert.Equal(new[] { "web_search", "web_fetch" }, profile.ToolNames);
        Assert.Equal(ModelRole.Main, profile.ModelRole);
        Assert.Equal(120, profile.TimeoutSeconds);
        Assert.True(profile.EmitStructuredFindings);
        Assert.Equal(SubAgentVisibility.UserFacing, profile.Visibility);
    }

    [Fact]
    public void LoadAll_defaults_model_role_to_compaction_and_timeout_to_60()
    {
        WriteAgent("minimal.md", """
            ---
            name: minimal
            description: Minimal agent
            tools: [web_search]
            ---

            You are minimal.
            """);

        var results = _loader.LoadAll();

        Assert.Single(results);
        Assert.Equal(ModelRole.Compaction, results[0].ModelRole);
        Assert.Equal(60, results[0].TimeoutSeconds);
        Assert.False(results[0].EmitStructuredFindings);
        Assert.Equal(SubAgentVisibility.UserFacing, results[0].Visibility);
    }

    [Fact]
    public void LoadAll_skips_file_with_no_frontmatter()
    {
        WriteAgent("bare.md", "Just a body. No frontmatter at all.");

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_malformed_frontmatter()
    {
        WriteAgent("broken.md", """
            ---
            name: broken
            description: [unclosed list
            ---

            body
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_agent_missing_name()
    {
        WriteAgent("noname.md", """
            ---
            description: Has a description but no name
            tools: [web_search]
            ---

            body
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_agent_missing_description()
    {
        WriteAgent("nodesc.md", """
            ---
            name: nodesc
            tools: [web_search]
            ---

            body
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_agent_with_empty_body()
    {
        WriteAgent("empty-body.md", """
            ---
            name: empty-body
            description: Has frontmatter but nothing after it
            tools: [web_search]
            ---
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_agent_with_no_tools()
    {
        WriteAgent("no-tools.md", """
            ---
            name: no-tools
            description: A test agent
            tools: []
            ---

            You are a test agent.
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_skips_user_facing_agent_with_disallowed_tools()
    {
        WriteAgent("bad-tools.md", """
            ---
            name: bad-tools
            description: A test agent
            tools: [web_search, file_write, shell_execute]
            ---

            You are a test agent.
            """);

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void LoadAll_rejects_duplicate_names_across_files()
    {
        WriteAgent("first.md", """
            ---
            name: shared
            description: First agent
            tools: [web_search]
            ---

            First body.
            """);
        WriteAgent("second.md", """
            ---
            name: shared
            description: Second agent
            tools: [web_fetch]
            ---

            Second body.
            """);

        var results = _loader.LoadAll();

        Assert.Single(results);
        // Deterministic ordering picks the first file alphabetically.
        Assert.Equal("First agent", results[0].Description);
    }

    [Fact]
    public void LoadAll_accepts_pascal_case_visibility_and_hyphenated_visibility()
    {
        WriteAgent("hyphenated.md", """
            ---
            name: hyphenated
            description: Hyphenated visibility value
            tools: [web_search]
            visibility: user-facing
            ---

            body
            """);
        WriteAgent("pascal.md", """
            ---
            name: pascal
            description: PascalCase visibility value
            tools: [web_search]
            visibility: UserFacing
            ---

            body
            """);

        var results = _loader.LoadAll();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(SubAgentVisibility.UserFacing, r.Visibility));
    }

    [Fact]
    public void LoadAll_ignores_non_md_files()
    {
        WriteAgent("ignored.json", """{"name":"json","description":"ignored"}""");
        WriteAgent("ignored.txt", "plain text");

        var results = _loader.LoadAll();

        Assert.Empty(results);
    }
}
