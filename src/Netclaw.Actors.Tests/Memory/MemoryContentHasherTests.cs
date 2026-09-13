// -----------------------------------------------------------------------
// <copyright file="MemoryContentHasherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class MemoryContentHasherTests
{
    [Fact]
    public void ComputeHash_is_case_insensitive()
    {
        var lower = MemoryContentHasher.ComputeHash("netclaw source location", "the repo lives on github");
        var upper = MemoryContentHasher.ComputeHash("NETCLAW SOURCE LOCATION", "THE REPO LIVES ON GITHUB");

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void ComputeHash_collapses_whitespace_differences()
    {
        var tight = MemoryContentHasher.ComputeHash("title", "one two three");
        var loose = MemoryContentHasher.ComputeHash("title", "one   two\tthree\n");

        Assert.Equal(tight, loose);
    }

    [Fact]
    public void ComputeHash_is_deterministic()
    {
        var h1 = MemoryContentHasher.ComputeHash("Netclaw memory redesign", "Use sqlite-backed automatic recall.");
        var h2 = MemoryContentHasher.ComputeHash("Netclaw memory redesign", "Use sqlite-backed automatic recall.");

        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ComputeHash_distinguishes_different_content()
    {
        var a = MemoryContentHasher.ComputeHash("title", "body one");
        var b = MemoryContentHasher.ComputeHash("title", "body two");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeHash_distinguishes_title_from_body_content()
    {
        // Swapping title/body content must not collide, even though the normalized
        // concatenation contains the same tokens overall.
        var a = MemoryContentHasher.ComputeHash("alpha", "beta");
        var b = MemoryContentHasher.ComputeHash("beta", "alpha");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeHash_produces_lowercase_hex_sha256()
    {
        var hash = MemoryContentHasher.ComputeHash("t", "b");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant(), StringComparer.Ordinal);
        Assert.True(hash.All(c => Uri.IsHexDigit(c)));
    }
}
