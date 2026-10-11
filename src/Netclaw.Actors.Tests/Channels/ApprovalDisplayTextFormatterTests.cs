// -----------------------------------------------------------------------
// <copyright file="ApprovalDisplayTextFormatterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class ApprovalDisplayTextFormatterTests
{
    [Fact]
    public void Text_under_budget_passes_through_unchanged()
    {
        Assert.Equal("git status", ApprovalDisplayTextFormatter.Truncate("git status", 100));
    }

    [Fact]
    public void Null_or_empty_returns_empty()
    {
        Assert.Equal(string.Empty, ApprovalDisplayTextFormatter.Truncate(null, 100));
        Assert.Equal(string.Empty, ApprovalDisplayTextFormatter.Truncate(string.Empty, 100));
    }

    [Fact]
    public void Zero_or_negative_budget_returns_empty()
    {
        Assert.Equal(string.Empty, ApprovalDisplayTextFormatter.Truncate("anything", 0));
        Assert.Equal(string.Empty, ApprovalDisplayTextFormatter.Truncate("anything", -1));
    }

    [Fact]
    public void Oversized_text_stays_within_budget()
    {
        var input = new string('x', 10_000);

        var result = ApprovalDisplayTextFormatter.Truncate(input, 200);

        Assert.True(result.Length <= 200, $"Result length {result.Length} exceeded budget 200");
        var shown = result.Count(static c => c == 'x');
        Assert.Contains($" … {10_000 - shown} characters hidden … ", result);
    }

    [Fact]
    public void Truncated_output_preserves_head_and_tail()
    {
        var input = "AAAAAAAAAA" + new string('m', 1000) + "ZZZZZZZZZZ";

        var result = ApprovalDisplayTextFormatter.Truncate(input, 200);

        Assert.StartsWith("AAAAAAAAAA", result);
        Assert.EndsWith("ZZZZZZZZZZ", result);
    }

    [Fact]
    public void List_bounds_each_item_and_names_the_items_it_does_not_show()
    {
        string[] items = ["git push", new string('g', 5_000), "ls", "cat", "rm"];

        var result = ApprovalDisplayTextFormatter.TruncateList(
            items, maxItemChars: 100, maxTotalChars: 150, perItemOverhead: 5);

        Assert.Equal("git push", result[0]);
        Assert.Equal(100, result[1].Length);
        Assert.Equal("ls", result[2]);
        Assert.Equal("… 2 more not shown", result[^1]);
        Assert.Equal(4, result.Count);
        Assert.True(result.Sum(static item => item.Length + 5) <= 150);
    }

    [Fact]
    public void List_that_fits_passes_through_unchanged()
    {
        string[] items = ["git push", "ls"];

        Assert.Equal(items, ApprovalDisplayTextFormatter.TruncateList(items, 100, 1_000, 5));
    }
}
