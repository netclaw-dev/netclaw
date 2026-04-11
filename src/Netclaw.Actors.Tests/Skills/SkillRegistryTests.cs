using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillRegistryTests
{
    private static SkillEntry MakeEntry(
        string name,
        string description = "desc",
        string? category = null,
        bool disableModelInvocation = false,
        string? allowedTools = null) =>
        new(name, name, description, $"/skills/{name}/SKILL.md", $"/skills/{name}", category)
        {
            DisableModelInvocation = disableModelInvocation,
            AllowedTools = allowedTools
        };

    [Fact]
    public void Search_matches_by_name()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("git-workflow"));
        registry.Register(MakeEntry("docker-deploy"));

        var results = registry.Search("git");

        Assert.Single(results);
        Assert.Equal("git-workflow", results[0].Name);
    }

    [Fact]
    public void Search_matches_by_description()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("deploy", "How to deploy containers"));

        var results = registry.Search("containers");

        Assert.Single(results);
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("Git-Workflow"));

        var results = registry.Search("GIT");

        Assert.Single(results);
    }

    [Fact]
    public void Search_returns_empty_for_no_match()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("git-workflow"));

        var results = registry.Search("kubernetes");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_respects_max_results()
    {
        var registry = new SkillRegistry();
        for (var i = 0; i < 10; i++)
            registry.Register(MakeEntry($"skill-{i}", "common keyword"));

        var results = registry.Search("common", maxResults: 3);

        Assert.Equal(3, results.Count);
    }

    // --- Index format tests ---

    [Fact]
    public void GenerateIndex_returns_empty_when_no_skills()
    {
        var registry = new SkillRegistry();
        Assert.Equal(string.Empty, registry.GenerateIndex("/test/skills"));
    }

    [Fact]
    public void GenerateIndex_includes_skill_file_path()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "Short description"));

        var index = registry.GenerateIndex("/test/skills");

        Assert.Contains("my-skill/SKILL.md", index);
    }

    [Fact]
    public void GenerateIndex_includes_root_path()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex("/home/user/.netclaw/skills");

        Assert.Contains("[skills]|root: /home/user/.netclaw/skills", index);
    }

    [Fact]
    public void GenerateIndex_groups_by_category()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-memory", "Memory guidance", ".system"));
        registry.Register(MakeEntry("my-workflow", "Workflow help"));

        var index = registry.GenerateIndex("/test/skills");

        Assert.Contains("|.system:{netclaw-memory/SKILL.md}", index);
        Assert.Contains("|user:{my-workflow/SKILL.md}", index);
    }

    [Fact]
    public void DisableModelInvocation_skill_excluded_from_index()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations routing", ".system",
            disableModelInvocation: true));
        registry.Register(MakeEntry("memory", "Memory guidance", ".system"));

        var index = registry.GenerateIndex("/test/skills");

        Assert.DoesNotContain("ops/SKILL.md", index);
        Assert.Contains("memory/SKILL.md", index);
    }

    [Fact]
    public void Clear_resets_index()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "A user skill"));

        Assert.NotEqual(string.Empty, registry.GenerateIndex("/test/skills"));

        registry.Clear();

        Assert.Equal(string.Empty, registry.GenerateIndex("/test/skills"));
    }

    // --- Slash-command dispatch tests ---

    [Fact]
    public void TryResolveSlashCommand_resolves_known_command()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-operations", "Operations routing"));

        var resolved = registry.TryResolveSlashCommand("/netclaw-operations check health",
            out var skill, out var remainder);

        Assert.True(resolved);
        Assert.NotNull(skill);
        Assert.Equal("netclaw-operations", skill!.Name);
        Assert.Equal("check health", remainder);
    }

    [Fact]
    public void TryResolveSlashCommand_returns_false_for_unknown_command()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-operations", "Operations routing"));

        var resolved = registry.TryResolveSlashCommand("/nonexistent",
            out var skill, out var remainder);

        Assert.False(resolved);
        Assert.Null(skill);
    }

    [Fact]
    public void TryResolveSlashCommand_returns_false_for_non_slash_input()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var resolved = registry.TryResolveSlashCommand("just a regular message",
            out _, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveSlashCommand_handles_command_with_no_arguments()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("diag", "Diagnostics"));

        var resolved = registry.TryResolveSlashCommand("/diag", out var skill, out var remainder);

        Assert.True(resolved);
        Assert.Equal("diag", skill!.Name);
        Assert.Equal(string.Empty, remainder);
    }

    [Fact]
    public void Non_user_invocable_skill_excluded_from_slash_commands()
    {
        var registry = new SkillRegistry();
        var entry = new SkillEntry("bg-skill", "bg-skill", "Background guidance",
            "/skills/bg-skill/SKILL.md", "/skills/bg-skill", null)
        {
            UserInvocable = false
        };
        registry.Register(entry);

        var resolved = registry.TryResolveSlashCommand("/bg-skill", out _, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void GetAvailableSlashCommands_lists_user_invocable_skills()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations"));
        registry.Register(new SkillEntry("hidden", "hidden", "Hidden",
            "/skills/hidden/SKILL.md", "/skills/hidden", null) { UserInvocable = false });

        var commands = registry.GetAvailableSlashCommands();

        Assert.Single(commands);
        Assert.Equal("/ops", commands[0].Command);
    }

    [Fact]
    public void Clear_resets_slash_commands()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations"));

        Assert.True(registry.TryResolveSlashCommand("/ops", out _, out _));

        registry.Clear();

        Assert.False(registry.TryResolveSlashCommand("/ops", out _, out _));
    }

    // --- Multi-root index tests ---

    [Fact]
    public void GenerateIndex_with_external_sources_uses_roots_header()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var externalSources = new[]
        {
            new ResolvedExternalSource("claude-code", new[] { "/home/user/.claude/skills" }, true)
        };

        var index = registry.GenerateIndex("/home/user/.netclaw/skills", externalSources);

        Assert.Contains("roots: native=/home/user/.netclaw/skills,claude-code=/home/user/.claude/skills", index);
        Assert.DoesNotContain("[skills]|root:", index);
    }

    [Fact]
    public void GenerateIndex_with_multi_path_external_source_joins_paths_with_semicolon()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var externalSources = new[]
        {
            new ResolvedExternalSource(
                "claude-code",
                new[]
                {
                    "/home/user/.claude/skills",
                    "/home/user/.claude/commands",
                    "/home/user/.claude/plugins/marketplaces/dotnet-skills/skills"
                },
                true)
        };

        var index = registry.GenerateIndex("/home/user/.netclaw/skills", externalSources);

        Assert.Contains(
            "claude-code=/home/user/.claude/skills;/home/user/.claude/commands;/home/user/.claude/plugins/marketplaces/dotnet-skills/skills",
            index);
    }

    [Fact]
    public void GenerateIndex_without_external_sources_uses_single_root_header()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex("/home/user/.netclaw/skills");

        Assert.Contains("[skills]|root: /home/user/.netclaw/skills", index);
        Assert.DoesNotContain("roots:", index);
    }

    [Fact]
    public void GenerateIndex_with_empty_external_sources_uses_single_root_header()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex("/home/user/.netclaw/skills", Array.Empty<ResolvedExternalSource>());

        Assert.Contains("[skills]|root: /home/user/.netclaw/skills", index);
    }
}
