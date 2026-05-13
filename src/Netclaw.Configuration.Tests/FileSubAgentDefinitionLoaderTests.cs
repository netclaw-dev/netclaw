// -----------------------------------------------------------------------
// <copyright file="FileSubAgentDefinitionLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public class FileSubAgentDefinitionLoaderTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly ListLogger<FileSubAgentDefinitionLoader> _logger;
    private readonly FileSubAgentDefinitionLoader _loader;

    public FileSubAgentDefinitionLoaderTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _logger = new ListLogger<FileSubAgentDefinitionLoader>();
        _loader = new FileSubAgentDefinitionLoader(_paths, _logger);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    private string WriteAgent(string fileName, string content)
    {
        var path = Path.Combine(_paths.AgentsDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadAll_logs_warning_when_agents_directory_is_missing()
    {
        Directory.Delete(_paths.AgentsDirectory, recursive: true);

        var results = _loader.LoadAll();

        Assert.Empty(results);
        Assert.Contains(_logger.Warnings, w =>
            w.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            && w.Contains(_paths.AgentsDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAll_logs_warning_when_agents_directory_has_no_markdown_files()
    {
        var results = _loader.LoadAll();

        Assert.Empty(results);
        Assert.Contains(_logger.Warnings, w =>
            w.Contains("No agent definition files found", StringComparison.OrdinalIgnoreCase)
            && w.Contains(_paths.AgentsDirectory, StringComparison.Ordinal));
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

        var profile = Assert.Single(results);
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

        var profile = Assert.Single(results);
        Assert.Equal(ModelRole.Compaction, profile.ModelRole);
        Assert.Equal(60, profile.TimeoutSeconds);
        Assert.False(profile.EmitStructuredFindings);
        Assert.Equal(SubAgentVisibility.UserFacing, profile.Visibility);
    }

    [Fact]
    public void LoadAll_accepts_hyphenated_and_pascal_case_visibility()
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
    public void LoadAll_duplicate_name_keeps_first_alphabetically_and_logs_the_rejection()
    {
        WriteAgent("a-wins.md", """
            ---
            name: shared
            description: First agent
            tools: [web_search]
            ---

            First body.
            """);
        WriteAgent("b-loses.md", """
            ---
            name: shared
            description: Second agent
            tools: [web_fetch]
            ---

            Second body.
            """);

        var results = _loader.LoadAll();

        var profile = Assert.Single(results);
        Assert.Equal("First agent", profile.Description);
        Assert.Contains("First body.", profile.SystemPrompt);

        // The second file should have produced a loud warning pointing at the duplicate.
        Assert.Contains(_logger.Warnings, w =>
            w.Contains("b-loses.md", StringComparison.Ordinal)
            && w.Contains("duplicate name", StringComparison.OrdinalIgnoreCase)
            && w.Contains("shared", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAll_loads_valid_agent_and_rejects_invalid_siblings_with_warnings()
    {
        // One valid agent — must survive loading alongside every invalid sibling below.
        WriteAgent("valid.md", """
            ---
            name: valid
            description: A real agent
            tools: [web_search]
            ---

            You are the only valid agent in this directory.
            """);

        // No frontmatter at all.
        WriteAgent("no-frontmatter.md", "Just a body. No YAML frontmatter delimiter.");

        // Frontmatter present but unparseable YAML.
        WriteAgent("malformed-yaml.md", """
            ---
            name: broken
            description: [unclosed list
            ---

            body
            """);

        // Frontmatter missing required name field.
        WriteAgent("missing-name.md", """
            ---
            description: Has description but no name
            tools: [web_search]
            ---

            body
            """);

        // Frontmatter missing required description field.
        WriteAgent("missing-description.md", """
            ---
            name: nodesc
            tools: [web_search]
            ---

            body
            """);

        // Valid frontmatter but empty body.
        WriteAgent("empty-body.md", """
            ---
            name: empty-body
            description: Frontmatter only
            tools: [web_search]
            ---
            """);

        // Empty tools list — valid, inherits all session tools at spawn time.
        WriteAgent("no-tools.md", """
            ---
            name: no-tools
            description: Has no tools specified
            tools: []
            ---

            body
            """);

        // Tools including MCP tools — valid, no allowlist restriction.
        WriteAgent("mcp-tools.md", """
            ---
            name: mcp-tools
            description: Uses MCP tools
            tools: [web_search, notion/notion-search, shell_execute]
            ---

            body
            """);

        // Non-markdown file dropped in the directory — should be silently ignored (not scanned at all).
        WriteAgent("stray.json", """{"name":"json","description":"legacy"}""");
        WriteAgent("readme.txt", "notes");

        var results = _loader.LoadAll();

        // Three valid agents: valid, no-tools, mcp-tools
        // (tools are now optional and there's no allowlist)
        Assert.Equal(3, results.Count);
        Assert.Contains(results, p => p.Name == "valid");
        Assert.Contains(results, p => p.Name == "no-tools");
        Assert.Contains(results, p => p.Name == "mcp-tools");

        // Negative: every invalid sibling produced a specific warning that names the file
        // AND points at the reason. This is the "fail loud" contract — without log checks,
        // a silent-skip regression would sail through the suite.
        Assert.Contains(_logger.Warnings, w =>
            w.Contains("no-frontmatter.md", StringComparison.Ordinal)
            && w.Contains("frontmatter", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(_logger.Warnings, w =>
            w.Contains("malformed-yaml.md", StringComparison.Ordinal)
            && w.Contains("frontmatter", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(_logger.Warnings, w =>
            w.Contains("missing-name.md", StringComparison.Ordinal)
            && w.Contains("name", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(_logger.Warnings, w =>
            w.Contains("missing-description.md", StringComparison.Ordinal)
            && w.Contains("description", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(_logger.Warnings, w =>
            w.Contains("empty-body.md", StringComparison.Ordinal)
            && w.Contains("system prompt body", StringComparison.OrdinalIgnoreCase));

        // The .json and .txt files must not have produced any warning at all — they should be
        // ignored at the Directory.GetFiles("*.md") pattern layer, never reaching the parser.
        Assert.DoesNotContain(_logger.Warnings, w => w.Contains("stray.json", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Warnings, w => w.Contains("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshIfChanged_detects_valid_edits_and_reloads_profiles()
    {
        var path = WriteAgent("reloadable.md", """
            ---
            name: reloadable
            description: First description
            tools: [file_read]
            ---

            First body.
            """);

        var first = _loader.LoadAll();
        var initial = Assert.Single(first);
        Assert.Equal("First description", initial.Description);

        File.WriteAllText(path, """
            ---
            name: reloadable
            description: Updated description
            tools: [file_read]
            ---

            Updated body.
            """);

        Assert.True(_loader.RefreshIfChanged(out var refreshed));
        var updated = Assert.Single(refreshed);
        Assert.Equal("Updated description", updated.Description);
        Assert.Contains("Updated body.", updated.SystemPrompt);
    }

    [Fact]
    public void RefreshIfChanged_detects_deletes_and_returns_empty_snapshot()
    {
        var path = WriteAgent("temporary.md", """
            ---
            name: temporary
            description: Temporary agent
            tools: [file_read]
            ---

            body
            """);

        Assert.Single(_loader.LoadAll());

        File.Delete(path);

        Assert.True(_loader.RefreshIfChanged(out var refreshed));
        Assert.Empty(refreshed);
    }

    [Fact]
    public void SyncInto_replaces_registry_profiles_when_disk_changes()
    {
        // Both spawn_agent and metadata.subagent routed activations go through the
        // same SyncInto contract — exercising the loader+registry pair end-to-end
        // proves the live-reload requirement for both entry points.
        var path = WriteAgent("routable.md", """
            ---
            name: routable
            description: First description
            tools: [file_read]
            ---

            First body.
            """);

        var registry = new SubAgentDefinitionRegistry();
        Assert.True(_loader.SyncInto(registry));
        Assert.Equal("First description", registry.TryGetByName("routable")!.Description);

        File.WriteAllText(path, """
            ---
            name: routable
            description: Updated description
            tools: [file_read]
            ---

            Updated body.
            """);

        Assert.True(_loader.SyncInto(registry));
        Assert.Equal("Updated description", registry.TryGetByName("routable")!.Description);
    }

    [Fact]
    public void SyncInto_is_a_no_op_when_directory_unchanged()
    {
        WriteAgent("stable.md", """
            ---
            name: stable
            description: Stable
            tools: [file_read]
            ---

            body
            """);

        var registry = new SubAgentDefinitionRegistry();
        Assert.True(_loader.SyncInto(registry));
        Assert.False(_loader.SyncInto(registry));
    }
}

/// <summary>
/// Capturing <see cref="ILogger{T}"/> that records formatted warning messages into a list.
/// Used to verify the "fail loud" contract — a loader that silently skips invalid files
/// would produce zero warnings and break these assertions instead of sailing through.
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}
