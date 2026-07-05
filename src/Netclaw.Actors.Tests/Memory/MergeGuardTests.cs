// -----------------------------------------------------------------------
// <copyright file="MergeGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Table/property tests for <see cref="MergeGuard"/> (memory-core-redesign Slice 3 task 3.3):
/// load-bearing token extraction (URLs, numbers/versions/dates, identifiers, file paths), the
/// 95% retention boundary, the 60% length-collapse floor, and pass-through on faithful unions.
/// </summary>
public sealed class MergeGuardTests
{
    // ── Faithful merges pass ─────────────────────────────────────────

    [Fact]
    public void Validate_passes_when_merged_body_is_a_faithful_union()
    {
        var sources = new[]
        {
            "Widget specs: 16 cores, 64GB RAM, 2 NICs.",
            "Widget pricing is TBD as of 2026-05-13."
        };
        var merged = "Widget specs: 16 cores, 64GB RAM, 2 NICs. Pricing is TBD as of 2026-05-13.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingTokens);
    }

    [Fact]
    public void Validate_passes_when_merged_body_reorders_and_rewords_but_keeps_every_token()
    {
        var sources = new[]
        {
            "Akka.NET GitHub repository: https://github.com/akkadotnet/akka.net. Latest stable release is 1.5.60 as of 2026-04-02.",
            "Akka.NET release version is now 1.5.62."
        };
        var merged =
            "Akka.NET GitHub repository: https://github.com/akkadotnet/akka.net. " +
            "Latest stable release is 1.5.62 (previously 1.5.60 as of 2026-04-02).";

        var result = MergeGuard.Validate(sources, merged);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Validate_passes_trivially_when_there_are_no_source_bodies()
    {
        var result = MergeGuard.Validate([], "anything");

        Assert.True(result.Passed);
        Assert.Contains("no source bodies", result.Reason);
    }

    // ── Token-category retention ─────────────────────────────────────

    [Fact]
    public void Validate_fails_when_a_url_is_dropped()
    {
        var sources = new[] { "Repo lives at https://github.com/netclaw-dev/netclaw." };
        var merged = "Repo lives at the usual place.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.False(result.Passed);
        Assert.Contains(result.MissingTokens, t => t.Contains("github.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_fails_when_a_version_number_is_dropped()
    {
        var sources = new[] { "Latest version is 1.5.62, released with the new serializer." };
        var merged = "Latest version was released with the new serializer.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.False(result.Passed);
        Assert.Contains("1.5.62", result.MissingTokens);
    }

    [Fact]
    public void Validate_fails_when_a_date_is_dropped()
    {
        var sources = new[] { "Config path moved to /etc/app/config.yaml on 2026-06-01." };
        var merged = "Config path moved to /etc/app/config.yaml.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.False(result.Passed);
        Assert.Contains("2026-06-01", result.MissingTokens);
    }

    [Fact]
    public void Validate_fails_when_a_written_date_is_dropped()
    {
        var sources = new[] { "Release shipped on May 13, 2026 after code freeze." };
        var merged = "Release shipped after code freeze.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Validate_fails_when_a_camelCase_identifier_is_dropped()
    {
        var sources = new[] { "The knob is called maxOutputTokens and defaults to 4096." };
        var merged = "The token cap defaults to 4096.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.False(result.Passed);
        Assert.Contains("maxOutputTokens", result.MissingTokens);
    }

    [Fact]
    public void Validate_fails_when_a_file_path_is_dropped()
    {
        var sources = new[] { "The guard lives in src/Netclaw.Actors/Memory/MergeGuard.cs." };
        var merged = "The guard lives in the memory module.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.False(result.Passed);
        Assert.Contains(result.MissingTokens, t => t.Contains("MergeGuard.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_retention_is_case_insensitive()
    {
        var sources = new[] { "Endpoint is HTTPS://EXAMPLE.COM/api." };
        var merged = "Endpoint is https://example.com/api and it is stable.";

        var result = MergeGuard.Validate(sources, merged);

        Assert.True(result.Passed);
    }

    // ── 95% retention boundary ────────────────────────────────────────

    [Fact]
    public void Validate_passes_at_exactly_the_95_percent_retention_boundary()
    {
        // 20 distinct load-bearing integers; merged keeps 19/20 = 95% exactly.
        var tokens = Enumerable.Range(100, 20).Select(n => n.ToString()).ToArray();
        var source = "Values: " + string.Join(", ", tokens) + ".";
        var merged = "Values: " + string.Join(", ", tokens.Take(19)) + ".";

        var result = MergeGuard.Validate([source], merged);

        Assert.True(result.Passed);
        Assert.Single(result.MissingTokens);
    }

    [Fact]
    public void Validate_fails_just_below_the_95_percent_retention_boundary()
    {
        // Same 20 tokens; merged keeps 18/20 = 90%, below the floor.
        var tokens = Enumerable.Range(100, 20).Select(n => n.ToString()).ToArray();
        var source = "Values: " + string.Join(", ", tokens) + ".";
        var merged = "Values: " + string.Join(", ", tokens.Take(18)) + ".";

        var result = MergeGuard.Validate([source], merged);

        Assert.False(result.Passed);
        Assert.Equal(2, result.MissingTokens.Count);
    }

    [Fact]
    public void Validate_counts_the_union_across_multiple_sources_not_per_source()
    {
        var sourceA = "Value alpha is 111.";
        var sourceB = "Value beta is 222.";
        // Merged keeps only one of the two distinct tokens across the union of both sources.
        var merged = "Value alpha is 111 and beta was updated.";

        var result = MergeGuard.Validate([sourceA, sourceB], merged);

        Assert.False(result.Passed);
        Assert.Contains("222", result.MissingTokens);
    }

    // ── Length-collapse floor ─────────────────────────────────────────

    [Fact]
    public void Validate_fails_on_length_collapse_even_when_tokens_are_retained()
    {
        // Merged repeats every load-bearing token but discards all surrounding prose,
        // collapsing well below 60% of the longest source's length.
        var longSource = "Config value is 42. " + new string('x', 200);
        var merged = "42";

        var result = MergeGuard.Validate([longSource], merged);

        Assert.False(result.Passed);
        Assert.Contains("collapse", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_passes_at_exactly_the_60_percent_length_boundary()
    {
        var longSource = new string('a', 100);
        var merged = new string('a', 60);

        var result = MergeGuard.Validate([longSource], merged);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Validate_fails_just_below_the_60_percent_length_boundary()
    {
        var longSource = new string('a', 100);
        var merged = new string('a', 59);

        var result = MergeGuard.Validate([longSource], merged);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Validate_uses_the_longest_source_for_the_length_floor()
    {
        var shortSource = "Short note.";
        var longSource = new string('b', 200);
        // Merged is well above 60% of the SHORT source but not the long one.
        var merged = new string('b', 100);

        var result = MergeGuard.Validate([shortSource, longSource], merged);

        Assert.False(result.Passed);
    }

    // ── Empty/null handling ────────────────────────────────────────────

    [Fact]
    public void Validate_treats_empty_source_bodies_as_contributing_nothing()
    {
        var result = MergeGuard.Validate(["", "  ", "real content here"], "real content here, unchanged");

        Assert.True(result.Passed);
    }
}
