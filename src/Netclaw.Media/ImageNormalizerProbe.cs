// -----------------------------------------------------------------------
// <copyright file="ImageNormalizerProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SkiaSharp;

namespace Netclaw.Media;

/// <summary>
/// Self-test for the native imaging library. The egress normalizer depends on
/// SkiaSharp's native <c>libSkiaSharp</c>, which is bundled into the self-contained
/// single-file binary and self-extracted at runtime. If a packaging regression drops
/// it, the first image op would throw deep in the channel/tool pipeline; this probe
/// surfaces the failure loudly up front (used by <c>netclaw doctor</c>).
/// </summary>
public static class ImageNormalizerProbe
{
    /// <summary>
    /// Runs a tiny encode → normalize round-trip to confirm the native imaging library
    /// is present and functional.
    /// </summary>
    public static ImagingProbeResult Probe()
    {
        try
        {
            byte[] png;
            using (var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Opaque)))
            {
                using (var canvas = new SKCanvas(bitmap))
                    canvas.Clear(SKColors.Black);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null || data.Size == 0)
                    return ImagingProbeResult.Failed("encode produced no output");
                png = data.ToArray();
            }

            var result = new SkiaImageNormalizer().Normalize(png, new ImageNormalizationOptions());
            return result.Outcome == ImageNormalizationOutcome.Dropped
                ? ImagingProbeResult.Failed($"normalize failed: {result.Reason}")
                : ImagingProbeResult.Working;
        }
        catch (Exception ex)
        {
            // DllNotFoundException / TypeInitializationException when the native lib
            // is missing; any other failure is equally a reason to fail the probe.
            return ImagingProbeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Result of <see cref="ImageNormalizerProbe.Probe"/>: working, or a failure reason.</summary>
public readonly record struct ImagingProbeResult(bool IsWorking, string? Error)
{
    public static ImagingProbeResult Working => new(true, null);

    public static ImagingProbeResult Failed(string error) => new(false, error);
}
