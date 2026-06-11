// -----------------------------------------------------------------------
// <copyright file="ImageDecodeMathTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Media.Tests;

/// <summary>
/// Pure decode-bound math — no native codec, so these run anywhere. The sample-size
/// is what keeps the JPEG/WebP decode from materializing a full-resolution bitmap.
/// </summary>
public sealed class ImageDecodeMathTests
{
    [Theory]
    [InlineData(8000, 8000, 1568, 4)]   // 8000/4=2000 ≥ 1568; 8000/8=1000 < 1568
    [InlineData(8000, 2000, 1568, 4)]   // long edge drives the choice
    [InlineData(4000, 3000, 1568, 2)]   // 4000/2=2000 ≥ 1568; 4000/4=1000 < 1568
    [InlineData(20000, 100, 1568, 8)]   // clamped at the JPEG DCT limit of 8
    [InlineData(1568, 1568, 1568, 1)]   // exactly at the cap → no scaling
    [InlineData(800, 600, 1568, 1)]     // already smaller than the cap
    public void ChooseDecodeSampleSize_picks_largest_safe_power_of_two(
        int w, int h, int cap, int expected)
    {
        Assert.Equal(expected, ImageDecodeMath.ChooseDecodeSampleSize(w, h, cap));
    }

    [Fact]
    public void ChooseDecodeSampleSize_never_undershoots_the_cap()
    {
        // The scaled long edge must stay ≥ cap so a final precise resize doesn't upscale.
        foreach (var longEdge in new[] { 1600, 3000, 5000, 8000, 16000 })
        {
            var sample = ImageDecodeMath.ChooseDecodeSampleSize(longEdge, longEdge / 2, 1568);
            Assert.True(longEdge / sample >= 1568, $"longEdge={longEdge} sample={sample} undershot 1568");
        }
    }

    [Fact]
    public void ChooseDecodeSampleSize_is_deterministic()
    {
        Assert.Equal(
            ImageDecodeMath.ChooseDecodeSampleSize(8000, 6000, 1568),
            ImageDecodeMath.ChooseDecodeSampleSize(8000, 6000, 1568));
    }

    [Fact]
    public void ChooseDecodeSampleSize_handles_nonpositive_cap()
    {
        Assert.Equal(1, ImageDecodeMath.ChooseDecodeSampleSize(8000, 8000, 0));
        Assert.Equal(1, ImageDecodeMath.ChooseDecodeSampleSize(8000, 8000, -5));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(3, 4)]
    [InlineData(6, 8)]
    [InlineData(1024, 1368)]
    public void Base64Length_matches_standard_encoding(long bytes, long expected)
    {
        Assert.Equal(expected, ImageDecodeMath.Base64Length(bytes));
    }
}
