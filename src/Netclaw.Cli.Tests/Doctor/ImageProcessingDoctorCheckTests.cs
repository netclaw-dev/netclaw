// -----------------------------------------------------------------------
// <copyright file="ImageProcessingDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ImageProcessingDoctorCheckTests
{
    [Fact]
    public async Task Passes_when_native_imaging_library_is_available()
    {
        // The native library is bundled in the test binary, so the probe round-trip
        // succeeds — this also guards against a regression that fails to ship it.
        var check = new ImageProcessingDoctorCheck();

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Equal("Image Processing", result.Name);
    }
}
