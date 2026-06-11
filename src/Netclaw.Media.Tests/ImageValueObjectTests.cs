// -----------------------------------------------------------------------
// <copyright file="ImageValueObjectTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Media.Tests;

public sealed class ImageValueObjectTests
{
    [Theory]
    [InlineData(500, "500B")]            // sub-KiB stays bytes (guards the old "0MB" drop-message bug)
    [InlineData(2048, "2KB")]
    [InlineData(5 * 1024 * 1024, "5MB")]
    public void ByteSize_formats_human_readable(int bytes, string expected)
        => Assert.Equal(expected, new ByteSize(bytes).ToString());
}
