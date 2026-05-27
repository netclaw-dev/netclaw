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
        Assert.Contains("truncated, original 10000 chars", result);
    }

    [Fact]
    public void Truncated_output_preserves_head_and_tail()
    {
        var input = "AAAAAAAAAA" + new string('m', 1000) + "ZZZZZZZZZZ";

        var result = ApprovalDisplayTextFormatter.Truncate(input, 200);

        Assert.StartsWith("AAAAAAAAAA", result);
        Assert.EndsWith("ZZZZZZZZZZ", result);
    }
}
