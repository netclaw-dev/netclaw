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

    private static string ClaudeSkillsPath(string home) => Path.Combine(home, ".claude", "skills");

    private static string MarketplacesRoot(string home) => Path.Combine(home, ".claude", "plugins", "marketplaces");

    private static string MarketplaceSkillsPath(string home, string marketplace) =>
        Path.Combine(home, ".claude", "plugins", "marketplaces", marketplace, "skills");

    [Fact]
    public void ClaudeCode_resolves_only_the_skills_path_when_no_marketplaces_installed()
    {
        Directory.CreateDirectory(ClaudeSkillsPath(_homeDir));

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
        Assert.Single(source.Paths);
        Assert.EndsWith(Path.Combine(".claude", "skills"), source.Paths[0]);
    }

    [Fact]
    public void ClaudeCode_expands_every_installed_marketplace_with_a_skills_subdir()
    {
        Directory.CreateDirectory(ClaudeSkillsPath(_homeDir));
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "dotnet-skills"));
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "prose"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "claude-code", WellKnown = "claude-code", Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Equal(3, source.Paths.Count);
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine(".claude", "skills"), StringComparison.Ordinal));
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine("marketplaces", "dotnet-skills", "skills"), StringComparison.Ordinal));
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine("marketplaces", "prose", "skills"), StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeCode_skips_marketplaces_that_have_no_skills_subdir()
    {
        Directory.CreateDirectory(ClaudeSkillsPath(_homeDir));
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "dotnet-skills"));
        Directory.CreateDirectory(Path.Combine(MarketplacesRoot(_homeDir), "empty-marketplace"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "claude-code", WellKnown = "claude-code", Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Equal(2, source.Paths.Count);
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine(".claude", "skills"), StringComparison.Ordinal));
        Assert.Contains(source.Paths, p => p.EndsWith(Path.Combine("marketplaces", "dotnet-skills", "skills"), StringComparison.Ordinal));
        Assert.DoesNotContain(source.Paths, p => p.Contains("empty-marketplace", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeCode_resolves_marketplace_only_when_primary_skills_dir_is_missing()
    {
        // No ~/.claude/skills directory — user has Claude Code plugins but never created a
        // bare skills/ dir. The claude-code source should still resolve from the marketplace alone.
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "dotnet-skills"));

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
        Assert.EndsWith(Path.Combine("marketplaces", "dotnet-skills", "skills"), source.Paths[0]);
    }

    [Fact]
    public void ClaudeCode_does_not_crash_when_marketplaces_root_is_missing()
    {
        Directory.CreateDirectory(ClaudeSkillsPath(_homeDir));

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
    public void Marketplace_expansion_does_not_apply_to_open_code()
    {
        Directory.CreateDirectory(Path.Combine(_homeDir, ".open-code", "skills"));
        // Marketplaces exist under ~/.claude but this source is open-code, not claude-code.
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "dotnet-skills"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "open-code", WellKnown = "open-code", Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Single(source.Paths);
        Assert.EndsWith(Path.Combine(".open-code", "skills"), source.Paths[0]);
    }

    [Fact]
    public void Marketplace_expansion_does_not_apply_to_custom_path_sources()
    {
        var customDir = Path.Combine(_homeDir, "team-skills");
        Directory.CreateDirectory(customDir);
        // Marketplaces exist but this source uses a custom Path, not WellKnown=claude-code.
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "dotnet-skills"));

        var config = new ExternalSkillsConfig
        {
            Sources =
            {
                new ExternalSkillSource { Name = "team", Path = customDir, Enabled = true }
            }
        };

        var resolved = config.ResolveEnabledSources(_homeDir);

        var source = Assert.Single(resolved);
        Assert.Single(source.Paths);
        Assert.Equal(customDir, source.Paths[0]);
    }

    [Fact]
    public void Disabled_source_is_skipped_even_if_paths_exist()
    {
        Directory.CreateDirectory(ClaudeSkillsPath(_homeDir));
        Directory.CreateDirectory(MarketplaceSkillsPath(_homeDir, "dotnet-skills"));

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
        Directory.CreateDirectory(ClaudeSkillsPath(_homeDir));

        var probed = ExternalSkillsConfig.ProbeWellKnownSources(_homeDir);

        var result = Assert.Single(probed);
        Assert.Equal("claude-code", result.WellKnownAlias);
        Assert.EndsWith(Path.Combine(".claude", "skills"), result.ResolvedPath);
    }

    [Fact]
    public void Probe_returns_no_results_when_no_paths_exist()
    {
        var probed = ExternalSkillsConfig.ProbeWellKnownSources(_homeDir);

        Assert.Empty(probed);
    }

    [Fact]
    public void Probe_does_not_surface_commands_directory_as_a_skill_source()
    {
        // Claude Code's flat slash-command files live at ~/.claude/commands. They
        // use a different frontmatter schema and must not be reported as a
        // valid claude-code source even when the skills/ dir is absent.
        Directory.CreateDirectory(Path.Combine(_homeDir, ".claude", "commands"));

        var probed = ExternalSkillsConfig.ProbeWellKnownSources(_homeDir);

        Assert.Empty(probed);
    }

    [Fact]
    public void ResolveWellKnownPaths_returns_only_the_skills_path_for_claude_code()
    {
        var paths = ExternalSkillsConfig.ResolveWellKnownPaths("claude-code", _homeDir);

        Assert.Single(paths);
        Assert.EndsWith(Path.Combine(".claude", "skills"), paths[0]);
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
