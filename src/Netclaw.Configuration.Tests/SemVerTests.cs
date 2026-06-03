// -----------------------------------------------------------------------
// <copyright file="SemVerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Feeds;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SemVerTests
{
    [Theory]
    // Core (major.minor.patch) precedence.
    [InlineData("0.18.1", "0.19.0", true)]
    [InlineData("0.19.0", "0.18.1", false)]
    [InlineData("0.19.0", "0.19.0", false)]
    [InlineData("0.18.1", "0.18.2", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    // A stable release outranks its own prereleases.
    [InlineData("0.19.0-beta1", "0.19.0", true)]
    [InlineData("0.19.0", "0.19.0-beta1", false)]
    // Prerelease progression.
    [InlineData("0.19.0-beta1", "0.19.0-beta2", true)]
    [InlineData("0.19.0-beta2", "0.19.0-beta1", false)]
    [InlineData("0.19.0-beta1", "0.19.0-beta1", false)]
    // Cross-version: a higher-minor beta beats a lower stable patch.
    [InlineData("0.18.2", "0.19.0-beta1", true)]
    [InlineData("0.19.0-beta1", "0.18.2", false)]
    // Numeric prerelease identifiers have LOWER precedence than alphanumeric ones.
    [InlineData("1.0.0-1", "1.0.0-alpha", true)]
    [InlineData("1.0.0-alpha", "1.0.0-1", false)]
    // Dot-separated numeric identifiers compare numerically (the robust beta form).
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", true)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", false)]
    // The repo's dotted prerelease convention: beta.10 must outrank beta.2. The release
    // version gate rejects the non-dotted form (beta10), which would compare lexically
    // and (incorrectly) rank below beta2.
    [InlineData("0.19.0-beta.2", "0.19.0-beta.10", true)]
    [InlineData("0.19.0-beta.9", "0.19.0-beta.10", true)]
    // When a prefix is equal, more identifiers => higher precedence.
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", true)]
    // Build metadata is ignored.
    [InlineData("1.0.0+aaa", "1.0.0+bbb", false)]
    [InlineData("1.0.0", "1.0.0+bbb", false)]
    // Unparseable input is never "newer" (fail safe).
    [InlineData("garbage", "1.0.0", false)]
    [InlineData("1.0.0", "garbage", false)]
    public void IsNewer_FollowsSemVerPrecedence(string current, string candidate, bool expected)
        => Assert.Equal(expected, SemVer.IsNewer(current, candidate));

    [Theory]
    [InlineData("garbage", "1.0.0")]
    [InlineData("1.0.0", "")]
    [InlineData("1.2.3.4", "1.0.0")] // too many core components
    [InlineData("1.0.0-", "1.0.0")]  // empty prerelease identifier
    public void TryCompare_ReturnsFalse_ForUnparseable(string a, string b)
        => Assert.False(SemVer.TryCompare(a, b, out _));

    [Fact]
    public void TryCompare_TreatsBuildMetadataAsEqual()
    {
        Assert.True(SemVer.TryCompare("1.2.3+abc", "1.2.3+def", out var cmp));
        Assert.Equal(0, cmp);
    }
}
