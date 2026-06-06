// -----------------------------------------------------------------------
// <copyright file="ImageNormalizerProbeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Media.Tests;

public sealed class ImageNormalizerProbeTests
{
    [Fact]
    public void TryProbe_returns_null_when_native_imaging_works()
    {
        // Exercises the full encode → normalize round-trip against the real native
        // library. A non-null result here means a packaging/native-load regression.
        Assert.Null(ImageNormalizerProbe.TryProbe());
    }
}
