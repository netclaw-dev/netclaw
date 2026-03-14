using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillRegistryMatchTests
{
    private static SkillEntry MakeEntry(string name, string description = "desc", string? triggers = null) =>
        new(name, name, description, $"/skills/{name}/SKILL.md", $"/skills/{name}", null) { Triggers = triggers };

    [Fact]
    public void MatchByKeywords_returns_skill_above_threshold()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price", "product", "shop", "compare" });

        var results = registry.MatchByKeywords("I want to buy something at a good price");

        Assert.Single(results);
        Assert.Equal("search-citation", results[0].Skill.Name);
        Assert.True(results[0].Score >= 2);
    }

    [Fact]
    public void MatchByKeywords_single_overlap_below_threshold()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price", "product" });

        var results = registry.MatchByKeywords("I want to search for information");

        Assert.Empty(results);
    }

    [Fact]
    public void MatchByKeywords_excludes_already_loaded()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price", "product" });

        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "search-citation" };
        var results = registry.MatchByKeywords("buy at a good price", exclude);

        Assert.Empty(results);
    }

    [Fact]
    public void MatchByKeywords_respects_max_results()
    {
        var registry = new SkillRegistry();
        for (var i = 0; i < 5; i++)
        {
            var name = $"skill-{i}";
            registry.Register(MakeEntry(name));
            registry.SetEnrichedKeywords(name,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price" });
        }

        var results = registry.MatchByKeywords("buy at a good price", maxResults: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void MatchByKeywords_sorted_by_score_descending()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("low-score"));
        registry.SetEnrichedKeywords("low-score",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price" });

        registry.Register(MakeEntry("high-score"));
        registry.SetEnrichedKeywords("high-score",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price", "product", "shop" });

        var results = registry.MatchByKeywords("I want to buy a product at a good price");

        Assert.Equal(2, results.Count);
        Assert.Equal("high-score", results[0].Skill.Name);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void MatchByKeywords_empty_message_returns_empty()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price" });

        Assert.Empty(registry.MatchByKeywords(""));
        Assert.Empty(registry.MatchByKeywords(null!));
    }

    [Fact]
    public void MatchByKeywords_skips_skills_without_enriched_keywords()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("no-keywords"));

        var results = registry.MatchByKeywords("buy something at a good price");

        Assert.Empty(results);
    }

    [Fact]
    public void MatchByKeywords_case_insensitive()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price" });

        var results = registry.MatchByKeywords("BUY at a good PRICE");

        Assert.Single(results);
    }

    [Fact]
    public void MatchByKeywords_plural_normalization_matches()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "price", "flight" });

        // "prices" normalizes to "price", "flights" normalizes to "flight"
        var results = registry.MatchByKeywords("check prices for flights");

        Assert.Single(results);
        Assert.Equal(2, results[0].Score);
    }

    [Fact]
    public void Clear_also_clears_enriched_keywords()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("search-citation"));
        registry.SetEnrichedKeywords("search-citation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "buy", "price" });

        registry.Clear();

        Assert.Empty(registry.GetEnrichedKeywords());
        Assert.Empty(registry.MatchByKeywords("buy at a good price"));
    }
}
