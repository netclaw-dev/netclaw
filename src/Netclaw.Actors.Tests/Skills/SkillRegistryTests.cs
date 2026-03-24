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
        SkillTrustTier tier = SkillTrustTier.User,
        bool disableModelInvocation = false,
        string? allowedTools = null) =>
        new(name, name, description, $"/skills/{name}/SKILL.md", $"/skills/{name}", category)
        {
            TrustTier = tier,
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

    // --- Compressed index format tests ---

    [Fact]
    public void GenerateDescriptionMenu_uses_compressed_pipe_format()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-memory", "Memory tools and recall guidance", ".system",
            SkillTrustTier.System));

        var menu = registry.GenerateDescriptionMenu();

        Assert.Contains("[skills]|load via skill_load(name)|invoke via /name", menu);
        Assert.Contains("|.system:{netclaw-memory}", menu);
        Assert.DoesNotContain("path:", menu);
        Assert.DoesNotContain("MANDATORY", menu);
    }

    [Fact]
    public void GenerateDescriptionMenu_returns_empty_when_no_skills()
    {
        var registry = new SkillRegistry();
        Assert.Equal(string.Empty, registry.GenerateDescriptionMenu());
    }

    [Fact]
    public void GenerateDescriptionMenu_uses_truncated_description_as_fallback_trigger()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "Short description"));

        var menu = registry.GenerateDescriptionMenu();

        Assert.Contains("Short description", menu);
    }

    [Fact]
    public void GenerateDescriptionMenu_truncates_long_descriptions()
    {
        var longDesc = new string('x', 100);
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("verbose", longDesc));

        var menu = registry.GenerateDescriptionMenu();

        // Should be truncated to 60 chars (57 + "...")
        Assert.DoesNotContain(longDesc, menu);
        Assert.Contains("...", menu);
    }

    [Fact]
    public void GenerateDescriptionMenu_uses_enriched_trigger_phrase_when_available()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation", "REQUIRED before any web_search..."));

        registry.SetTriggerPhrases(new Dictionary<string, string>
        {
            ["search-citation"] = "web search, citations, source verification"
        });

        var menu = registry.GetMenuForAudience(TrustAudience.Personal);

        Assert.Contains("web search, citations, source verification", menu);
        Assert.DoesNotContain("REQUIRED before any web_search", menu);
    }

    // --- Audience filtering tests ---

    [Fact]
    public void DisableModelInvocation_skill_excluded_from_index()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations routing", ".system",
            SkillTrustTier.System, disableModelInvocation: true));
        registry.Register(MakeEntry("memory", "Memory guidance", ".system",
            SkillTrustTier.System));

        registry.RebuildAudienceMenus();
        var menu = registry.GetMenuForAudience(TrustAudience.Personal);

        Assert.DoesNotContain("ops", menu);
        Assert.Contains("memory", menu);
    }

    [Fact]
    public void Public_audience_sees_no_skills_by_default()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("system-skill", "A system skill", ".system",
            SkillTrustTier.System));
        registry.Register(MakeEntry("user-skill", "A user skill"));

        registry.RebuildAudienceMenus();
        var menu = registry.GetMenuForAudience(TrustAudience.Public);

        // All tiers default to Team minimum, so Public sees nothing
        Assert.Equal(string.Empty, menu);
    }

    [Fact]
    public void Team_audience_sees_system_user_and_community_skills()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("sys", "System skill", ".system", SkillTrustTier.System));
        registry.Register(MakeEntry("usr", "User skill", tier: SkillTrustTier.User));
        registry.Register(MakeEntry("comm", "Community skill", ".community", SkillTrustTier.Community));
        registry.Register(MakeEntry("ext", "External skill", ".external", SkillTrustTier.External));

        registry.RebuildAudienceMenus();
        var menu = registry.GetMenuForAudience(TrustAudience.Team);

        Assert.Contains("sys", menu);
        Assert.Contains("usr", menu);
        Assert.Contains("comm", menu);
        Assert.DoesNotContain("ext", menu);
    }

    [Fact]
    public void Personal_audience_sees_all_tiers()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("sys", "System skill", ".system", SkillTrustTier.System));
        registry.Register(MakeEntry("usr", "User skill", tier: SkillTrustTier.User));
        registry.Register(MakeEntry("ext", "External skill", ".external", SkillTrustTier.External));
        registry.Register(MakeEntry("agt", "Agent skill", ".agent", SkillTrustTier.Agent));

        registry.RebuildAudienceMenus();
        var menu = registry.GetMenuForAudience(TrustAudience.Personal);

        Assert.Contains("sys", menu);
        Assert.Contains("usr", menu);
        Assert.Contains("ext", menu);
        Assert.Contains("agt", menu);
    }

    // --- Per-audience menu caching tests ---

    [Fact]
    public void RebuildAudienceMenus_caches_per_audience()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "A user skill"));
        registry.RebuildAudienceMenus();

        var publicMenu = registry.GetMenuForAudience(TrustAudience.Public);
        var teamMenu = registry.GetMenuForAudience(TrustAudience.Team);
        var personalMenu = registry.GetMenuForAudience(TrustAudience.Personal);

        Assert.Equal(string.Empty, publicMenu);
        Assert.NotEqual(string.Empty, teamMenu);
        Assert.NotEqual(string.Empty, personalMenu);
    }

    [Fact]
    public void Clear_resets_audience_menus()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "A user skill"));
        registry.RebuildAudienceMenus();

        Assert.NotEqual(string.Empty, registry.GetMenuForAudience(TrustAudience.Personal));

        registry.Clear();

        Assert.Equal(string.Empty, registry.GetMenuForAudience(TrustAudience.Personal));
    }

    [Fact]
    public void GetMenuForAudience_returns_empty_before_rebuild()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "A user skill"));

        // No RebuildAudienceMenus() called
        Assert.Equal(string.Empty, registry.GetMenuForAudience(TrustAudience.Team));
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
}
