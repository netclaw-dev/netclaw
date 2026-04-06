using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public class ExternalSkillsConfigTests : IDisposable
{
    private readonly string _homeDir;

    public ExternalSkillsConfigTests()
    {
        _homeDir = Path.Combine(Path.GetTempPath(), $"netclaw-home-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_homeDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, recursive: true);
    }

    [Fact]
    public void ClaudeCode_resolves_to_one_source_with_both_paths_when_skills_and_commands_exist()
    {
        var skillsDir = Path.Combine(_homeDir, ".claude", "skills");
        var commandsDir = Path.Combine(_homeDir, ".claude", "commands");
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(commandsDir);

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "claude-code", WellKnown = "claude-code", Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Equal("claude-code", source.Name);
        Assert.Equal(2, source.Paths.Count);
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine(".claude", "skills"), StringComparison.Ordinal));
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine(".claude", "commands"), StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeCode_drops_missing_commands_path_silently()
    {
        Directory.CreateDirectory(Path.Combine(_homeDir, ".claude", "skills"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "claude-code", WellKnown = "claude-code", Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Single(source.Paths);
        Assert.EndsWith(Path.Combine(".claude", "skills"), source.Paths[0]);
    }

    [Fact]
    public void ClaudeCode_resolves_when_only_commands_dir_exists()
    {
        Directory.CreateDirectory(Path.Combine(_homeDir, ".claude", "commands"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "claude-code", WellKnown = "claude-code", Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Single(source.Paths);
        Assert.EndsWith(Path.Combine(".claude", "commands"), source.Paths[0]);
    }

    [Fact]
    public void Disabled_source_is_skipped_even_if_paths_exist()
    {
        Directory.CreateDirectory(Path.Combine(_homeDir, ".claude", "skills"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "claude-code", WellKnown = "claude-code", Enabled = false }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Probe_returns_one_result_per_alias_when_any_path_exists()
    {
        Directory.CreateDirectory(Path.Combine(_homeDir, ".claude", "skills"));

        var probed = ExternalSkillsConfig.ProbeWellKnownSources(_homeDir);

        var result = Assert.Single(probed);
        Assert.Equal("claude-code", result.WellKnownAlias);
        // Primary (skills) preferred when it exists
        Assert.EndsWith(Path.Combine(".claude", "skills"), result.ResolvedPath);
    }

    [Fact]
    public void Probe_returns_no_results_when_no_paths_exist()
    {
        var probed = ExternalSkillsConfig.ProbeWellKnownSources(_homeDir);

        Assert.Empty(probed);
    }

    [Fact]
    public void Probe_uses_commands_dir_as_fallback_when_primary_skills_missing()
    {
        Directory.CreateDirectory(Path.Combine(_homeDir, ".claude", "commands"));

        var probed = ExternalSkillsConfig.ProbeWellKnownSources(_homeDir);

        var result = Assert.Single(probed);
        Assert.EndsWith(Path.Combine(".claude", "commands"), result.ResolvedPath);
    }

    [Fact]
    public void ResolveWellKnownPaths_returns_all_paths_for_claude_code()
    {
        var paths = ExternalSkillsConfig.ResolveWellKnownPaths("claude-code", _homeDir);

        Assert.Equal(2, paths.Count);
        Assert.EndsWith(Path.Combine(".claude", "skills"), paths[0]);
        Assert.EndsWith(Path.Combine(".claude", "commands"), paths[1]);
    }

    [Fact]
    public void ResolveWellKnownPath_returns_primary_path_for_backward_compat()
    {
        var path = ExternalSkillsConfig.ResolveWellKnownPath("claude-code");

        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine(".claude", "skills"), path);
    }

    [Fact]
    public void ResolveWellKnownPath_returns_null_for_unknown_alias()
    {
        Assert.Null(ExternalSkillsConfig.ResolveWellKnownPath("not-a-real-alias"));
        Assert.Empty(ExternalSkillsConfig.ResolveWellKnownPaths("not-a-real-alias"));
    }

    [Fact]
    public void Custom_path_source_resolves_with_single_path()
    {
        var customDir = Path.Combine(_homeDir, "team-skills");
        Directory.CreateDirectory(customDir);

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource
                {
                    Name = "team",
                    Path = customDir,
                    Enabled = true
                }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Equal("team", source.Name);
        Assert.Single(source.Paths);
        Assert.Equal(customDir, source.Paths[0]);
    }
}
