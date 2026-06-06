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
    /// Runs a tiny encode → normalize round-trip. Returns <c>null</c> when imaging
    /// works, or a short error string describing why it does not.
    /// </summary>
    public static string? TryProbe()
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
                    return "encode produced no output";
                png = data.ToArray();
            }

            var result = new SkiaImageNormalizer().Normalize(png, new ImageNormalizationOptions());
            return result.Outcome == ImageNormalizationOutcome.Dropped
                ? $"normalize failed: {result.Reason}"
                : null;
        }
        catch (Exception ex)
        {
            // DllNotFoundException / TypeInitializationException when the native lib
            // is missing; any other failure is equally a reason to fail the probe.
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
