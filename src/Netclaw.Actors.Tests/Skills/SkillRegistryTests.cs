using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillRegistryTests
{
    private static SkillEntry MakeEntry(string name, string description = "desc", string? triggers = null) =>
        new(name, name, description, $"/skills/{name}/SKILL.md", $"/skills/{name}", null) { Triggers = triggers };

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
    public void GenerateCompressedIndex_lists_skills_with_paths_and_descriptions()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("identity-management", "How to edit identity files"));
        registry.Register(MakeEntry("self-diagnostics", "Check Netclaw configuration"));

        var index = registry.GenerateCompressedIndex();

        Assert.Contains("identity-management", index);
        Assert.Contains("/skills/identity-management/SKILL.md", index);
        Assert.Contains("How to edit identity files", index);
        Assert.Contains("self-diagnostics", index);
        Assert.Contains("/skills/self-diagnostics/SKILL.md", index);
        Assert.Contains("Check Netclaw configuration", index);
        Assert.Contains("LOAD these with file_read when your current situation matches a trigger", index);
        Assert.DoesNotContain("search_skills", index);
    }

    [Fact]
    public void GenerateCompressedIndex_returns_empty_when_no_skills()
    {
        var registry = new SkillRegistry();
        Assert.Equal(string.Empty, registry.GenerateCompressedIndex());
    }

    [Fact]
    public void GenerateCompressedIndex_includes_triggers_when_present()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("self-diagnostics", "Check health",
            triggers: "connection failure | session timeout"));

        var index = registry.GenerateCompressedIndex();

        Assert.Contains("LOAD WHEN: connection failure | session timeout", index);
    }

    [Fact]
    public void GenerateCompressedIndex_omits_load_when_for_skills_without_triggers()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("plain-skill", "No triggers here"));

        var index = registry.GenerateCompressedIndex();

        Assert.DoesNotContain("LOAD WHEN:", index);
    }

    [Fact]
    public void Search_matches_by_triggers()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("diagnostics", "Check health",
            triggers: "connection failure | session timeout"));
        registry.Register(MakeEntry("other-skill", "Unrelated"));

        var results = registry.Search("session timeout");

        Assert.Single(results);
        Assert.Equal("diagnostics", results[0].Name);
    }
}
