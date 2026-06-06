// -----------------------------------------------------------------------
// <copyright file="ImageProcessingDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Media;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Verifies the native imaging library (SkiaSharp) is present and functional.
/// Image attachments and <c>file_read</c> images are bounded for model input via
/// this library; if its native asset is missing from the packaged binary, image
/// egress fails at runtime. This check turns that packaging regression into a loud,
/// up-front diagnostic.
/// </summary>
public sealed class ImageProcessingDoctorCheck : IDoctorCheck
{
    private const string CheckName = "Image Processing";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var error = ImageNormalizerProbe.TryProbe();
        return Task.FromResult(error is null
            ? DoctorCheckResult.Pass(
                CheckName,
                "Native imaging library loaded; image egress normalization is available.")
            : DoctorCheckResult.Error(
                CheckName,
                $"Native imaging library failed to load: {error}. Image attachments and file_read "
                + "images cannot be bounded for model input.",
                "This is a packaging regression — the SkiaSharp native library is missing from the "
                + "binary. Reinstall from an official release; if building locally, publish via "
                + "scripts/build/publish-binaries.sh (self-contained single-file embeds native libs)."));
    }
}
