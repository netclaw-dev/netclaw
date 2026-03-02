using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillRegistryTests
{
    private static SkillEntry MakeEntry(string name, string description = "desc") =>
        new(name, name, description, $"/skills/{name}.md", null);

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
    public void GenerateCompressedIndex_lists_all_skills()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("identity-management"));
        registry.Register(MakeEntry("self-diagnostics"));

        var index = registry.GenerateCompressedIndex();

        Assert.Contains("identity-management", index);
        Assert.Contains("self-diagnostics", index);
        Assert.Contains("search_skills", index);
    }

    [Fact]
    public void GenerateCompressedIndex_returns_empty_when_no_skills()
    {
        var registry = new SkillRegistry();
        Assert.Equal(string.Empty, registry.GenerateCompressedIndex());
    }
}
