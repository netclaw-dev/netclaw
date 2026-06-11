// -----------------------------------------------------------------------
// <copyright file="TestImages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SkiaSharp;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Generates real, decodable image fixtures for tests. The egress image normalizer
/// (#1296) decodes and bounds every image, so tests can no longer use fake magic-byte
/// stubs — a real image is required or the normalizer drops it.
/// </summary>
public static class TestImages
{
    /// <summary>
    /// A real opaque PNG small enough that the egress normalizer passes it through
    /// byte-for-byte (so "media round-trips unchanged" assertions still hold).
    /// </summary>
    public static byte[] SmallPng(int size = 16)
        => Encode(size, size, SKEncodedImageFormat.Png, opaque: true, detail: false);

    /// <summary>
    /// A larger, detailed image — use when a test needs the normalizer to actually
    /// downscale/re-encode (e.g. an oversized source).
    /// </summary>
    public static byte[] Image(
        int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png, bool opaque = true)
        => Encode(width, height, format, opaque, detail: true);

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, bool opaque, bool detail)
    {
        var alpha = opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(opaque ? SKColors.CornflowerBlue : SKColors.Transparent);
            if (detail)
            {
                // High-entropy content so JPEG can't trivially compress it away —
                // needed when a test exercises the byte-budget shrink path.
                using var paint = new SKPaint { IsAntialias = true };
                var rand = new Random(1234); // seeded → deterministic fixtures
                var maxRadius = Math.Max(6, width / 10);
                for (var i = 0; i < 400; i++)
                {
                    paint.Color = new SKColor(
                        (byte)rand.Next(256), (byte)rand.Next(256), (byte)rand.Next(256),
                        opaque ? (byte)255 : (byte)rand.Next(256));
                    canvas.DrawCircle(rand.Next(width), rand.Next(height), rand.Next(5, maxRadius), paint);
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
