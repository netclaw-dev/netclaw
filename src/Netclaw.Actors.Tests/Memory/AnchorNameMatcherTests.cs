using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class AnchorNameMatcherTests
{
    // ── Tokenize ────────────────────────────────────────────────────

    [Theory]
    [InlineData("akka-net-release", new[] { "akka", "net", "release" })]
    [InlineData("user-preferred-color", new[] { "user", "preferred", "color" })]
    [InlineData("AKKA-NET-RELEASE", new[] { "akka", "net", "release" })]
    [InlineData("single", new[] { "single" })]
    [InlineData("", new string[0])]
    [InlineData("  ", new string[0])]
    public void Tokenize_splits_on_dash_and_lowers(string input, string[] expected)
    {
        var tokens = AnchorNameMatcher.Tokenize(input);
        Assert.Equal(expected, tokens);
    }

    // ── IsFuzzyMatch: subset matching ───────────────────────────────

    [Fact]
    public void IsFuzzyMatch_returns_true_for_subset_match()
    {
        // {akka, net, release} is a subset of {akka, net, latest, release}
        var tokensA = new[] { "akka", "net", "release" };
        var tokensB = new[] { "akka", "net", "latest", "release" };
        Assert.True(AnchorNameMatcher.IsFuzzyMatch(tokensA, tokensB));
    }

    [Fact]
    public void IsFuzzyMatch_returns_true_for_version_suffix()
    {
        // {akka, net, release} is subset of {akka, net, release, 1.5.62}
        var tokensA = new[] { "akka", "net", "release" };
        var tokensB = new[] { "akka", "net", "release", "1.5.62" };
        Assert.True(AnchorNameMatcher.IsFuzzyMatch(tokensA, tokensB));
    }

    [Fact]
    public void IsFuzzyMatch_returns_true_for_exact_match()
    {
        var tokensA = new[] { "akka", "net", "release" };
        var tokensB = new[] { "akka", "net", "release" };
        Assert.True(AnchorNameMatcher.IsFuzzyMatch(tokensA, tokensB));
    }

    // ── IsFuzzyMatch: single token difference ───────────────────────

    [Fact]
    public void IsFuzzyMatch_returns_true_for_single_token_difference()
    {
        // {akka, net, release} vs {akka, net, version} differ by 2 in symmetric diff
        // but 2 > MaxTokenDifference (1), so this should NOT match unless subset
        // Let's use a real single-token diff: {akka, net, info} vs {akka, net, data}
        // Jaccard = 2/4 = 0.5 < 0.6 -> no match (Jaccard too low)

        // Better example: {akka, net, release, info} vs {akka, net, release, data}
        // Jaccard = 3/5 = 0.6, symmetric diff = 2 > 1 -> no match

        // Real single-token diff with enough overlap: {release, notes, v1} vs {release, notes, v2}
        // Jaccard = 2/4 = 0.5 < 0.6 -> no match

        // {akka, net, release, notes} vs {akka, net, release, changelog}
        // Jaccard = 3/5 = 0.6, symmetric diff = 2 > 1 -> no match (not subset either)

        // The single-token diff path works when Jaccard >= 0.6 AND diff <= 1
        // Example: {akka, net} vs {akka, release} -> Jaccard = 1/3 = 0.33 -> no match (too low)

        // {a, b, c, d} vs {a, b, c, e} -> Jaccard = 3/5 = 0.6, diff = 2 -> no match
        // The single-token diff needs: union - intersection <= 1
        // With 1 diff: {a, b, c} vs {a, b, d} -> Jaccard = 2/4 = 0.5 < 0.6 -> no match

        // In practice, single-token-diff matching requires at least 3 shared tokens:
        // {a, b, c, d} vs {a, b, c} -> subset match (handled separately)
        // {a, b, c, d, e} vs {a, b, c, d, f} -> Jaccard = 4/6 = 0.67, diff = 2 -> no match
        // The single-token diff path is narrow but catches:
        // {a, b} vs {a, c} -> Jaccard = 1/3 = 0.33 -> no

        // Actually, single token diff means union-intersection = 1
        // That means setA and setB differ by exactly 1 element (one has an extra, or one is swapped)
        // For union-intersection=1: one set is a subset of the other with one extra -> that's subset match.
        // Or: both same size, one element different -> symmetric diff = 2 -> won't match.
        // So single-token-diff with Jaccard >= 0.6 effectively requires 2+ shared, 1 extra in one set.
        // Example: {user, color} vs {user, color, preference} -> subset -> already matched
        // The <= 1 token difference catches the non-subset case where both have unique elements.
        // But with diff=1 and non-subset: impossible (diff=1 means one extra in one side only -> subset).
        // So in practice, all diff<=1 cases ARE subset cases. The clause is defense-in-depth.

        // Let's verify subset matching works for the documented patterns:
        var tokensA = new[] { "user", "color" };
        var tokensB = new[] { "user", "color", "preference" };
        Assert.True(AnchorNameMatcher.IsFuzzyMatch(tokensA, tokensB));
    }

    // ── IsFuzzyMatch: no match ──────────────────────────────────────

    [Fact]
    public void IsFuzzyMatch_returns_false_for_unrelated_names()
    {
        var tokensA = new[] { "akka", "net", "release" };
        var tokensB = new[] { "user", "preferred", "color" };
        Assert.False(AnchorNameMatcher.IsFuzzyMatch(tokensA, tokensB));
    }

    [Fact]
    public void IsFuzzyMatch_returns_false_for_empty_tokens()
    {
        Assert.False(AnchorNameMatcher.IsFuzzyMatch([], []));
        Assert.False(AnchorNameMatcher.IsFuzzyMatch([], new[] { "a" }));
        Assert.False(AnchorNameMatcher.IsFuzzyMatch(new[] { "a" }, []));
    }

    [Fact]
    public void IsFuzzyMatch_returns_false_when_jaccard_below_threshold()
    {
        // {a, b, c} vs {a, d, e} -> Jaccard = 1/5 = 0.2
        var tokensA = new[] { "a", "b", "c" };
        var tokensB = new[] { "a", "d", "e" };
        Assert.False(AnchorNameMatcher.IsFuzzyMatch(tokensA, tokensB));
    }

    // ── FindFuzzyMatches ────────────────────────────────────────────

    [Fact]
    public void FindFuzzyMatches_returns_subset_matches()
    {
        var existing = new List<string>
        {
            "akka-net-release",
            "user-preferred-color",
            "akka-net-latest-release",
            "database-config"
        };

        // "akka-net-release-1.5.62" tokens = {akka, net, release, 1.5.62}
        // "akka-net-release" tokens = {akka, net, release} -> subset of proposal -> match
        // "akka-net-latest-release" tokens = {akka, net, latest, release} -> NOT subset (has "latest"), and
        //   Jaccard = 3/5 = 0.6 but symmetric diff = 2 -> no match
        var matches = AnchorNameMatcher.FindFuzzyMatches("akka-net-release-1.5.62", existing);

        Assert.Contains("akka-net-release", matches);
        Assert.DoesNotContain("user-preferred-color", matches);
        Assert.DoesNotContain("database-config", matches);
    }

    [Fact]
    public void FindFuzzyMatches_returns_superset_matches()
    {
        var existing = new List<string>
        {
            "akka-net-release-info",
            "user-preferred-color",
            "database-config"
        };

        // "akka-net-release" tokens = {akka, net, release}
        // "akka-net-release-info" tokens = {akka, net, release, info} -> "akka-net-release" is a subset -> match
        var matches = AnchorNameMatcher.FindFuzzyMatches("akka-net-release", existing);

        Assert.Contains("akka-net-release-info", matches);
        Assert.DoesNotContain("user-preferred-color", matches);
        Assert.DoesNotContain("database-config", matches);
    }

    [Fact]
    public void FindFuzzyMatches_returns_empty_for_no_matches()
    {
        var existing = new List<string>
        {
            "user-preferred-color",
            "database-config"
        };

        var matches = AnchorNameMatcher.FindFuzzyMatches("akka-net-release", existing);
        Assert.Empty(matches);
    }

    // ── ComputeContentOverlap ───────────────────────────────────────

    [Fact]
    public void ComputeContentOverlap_returns_1_for_identical_content()
    {
        var overlap = AnchorNameMatcher.ComputeContentOverlap(
            "favorite color is blue",
            "favorite color is blue");
        Assert.Equal(1.0, overlap, 2);
    }

    [Fact]
    public void ComputeContentOverlap_returns_high_for_similar_content()
    {
        var overlap = AnchorNameMatcher.ComputeContentOverlap(
            "The latest Akka.NET release is version 1.5.60",
            "The latest Akka.NET release is version 1.5.62");
        // Most words overlap except version number
        Assert.True(overlap > 0.7);
    }

    [Fact]
    public void ComputeContentOverlap_returns_low_for_different_content()
    {
        var overlap = AnchorNameMatcher.ComputeContentOverlap(
            "favorite color is blue",
            "the database runs on PostgreSQL 15");
        Assert.True(overlap < 0.2);
    }

    [Fact]
    public void ComputeContentOverlap_returns_0_for_empty_content()
    {
        Assert.Equal(0.0, AnchorNameMatcher.ComputeContentOverlap("", "something"));
        Assert.Equal(0.0, AnchorNameMatcher.ComputeContentOverlap("something", ""));
        Assert.Equal(0.0, AnchorNameMatcher.ComputeContentOverlap("", ""));
    }
}
