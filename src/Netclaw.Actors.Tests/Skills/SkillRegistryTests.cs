using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillRegistryTests
{
    private static SkillEntry MakeEntry(string name, string description = "desc") =>
        new(name, name, description, $"/skills/{name}/SKILL.md", $"/skills/{name}", null);

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

    [Fact]
    public void GenerateDescriptionMenu_lists_skills_with_paths_and_descriptions()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-identity", "How to edit identity files"));
        registry.Register(MakeEntry("netclaw-diagnostics", "Check Netclaw configuration"));

        var menu = registry.GenerateDescriptionMenu();

        Assert.Contains("[available-skills", menu);
        Assert.Contains("MUST", menu);
        Assert.Contains("- netclaw-identity: How to edit identity files", menu);
        Assert.Contains("path: /skills/netclaw-identity/SKILL.md", menu);
        Assert.Contains("- netclaw-diagnostics: Check Netclaw configuration", menu);
        Assert.Contains("path: /skills/netclaw-diagnostics/SKILL.md", menu);
    }

    [Fact]
    public void GenerateDescriptionMenu_returns_empty_when_no_skills()
    {
        var registry = new SkillRegistry();
        Assert.Equal(string.Empty, registry.GenerateDescriptionMenu());
    }

    [Fact]
    public void GenerateDescriptionMenu_includes_resource_count_when_present()
    {
        var registry = new SkillRegistry();
        var entry = new SkillEntry("web-search", "Web Search", "Search the web",
            "/skills/web-search/SKILL.md", "/skills/web-search", null)
        {
            ResourcePaths = ["references/a.md", "references/b.md"]
        };
        registry.Register(entry);

        var menu = registry.GenerateDescriptionMenu();

        Assert.Contains("resources: [2 files in /skills/web-search]", menu);
    }

    [Fact]
    public void GenerateDescriptionMenu_omits_resources_when_none()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("plain-skill", "No resources here"));

        var menu = registry.GenerateDescriptionMenu();

        Assert.DoesNotContain("resources:", menu);
    }
}
