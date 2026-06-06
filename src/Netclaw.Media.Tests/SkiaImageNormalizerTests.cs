// -----------------------------------------------------------------------
// <copyright file="SkiaImageNormalizerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SkiaSharp;
using Xunit;

namespace Netclaw.Media.Tests;

/// <summary>
/// Behavioral tests for the egress image normalizer. Fixtures are generated in-test
/// with SkiaSharp (deterministic, no checked-in binaries).
/// </summary>
public sealed class SkiaImageNormalizerTests
{
    private readonly SkiaImageNormalizer _normalizer = new();

    [Fact]
    public void Oversized_by_dimension_is_downscaled_preserving_aspect()
    {
        var src = GenerateImage(4000, 3000, SKEncodedImageFormat.Png);

        var result = _normalizer.Normalize(src, new ImageNormalizationOptions());

        Assert.Equal(ImageNormalizationOutcome.Normalized, result.Outcome);
        var (w, h) = Dims(result.Bytes!);
        Assert.True(Math.Max(w, h) <= 1568, $"long edge {Math.Max(w, h)} exceeds cap");
        Assert.True(Math.Abs((double)w / h - 4000.0 / 3000.0) < 0.02, $"aspect drifted: {w}x{h}");
        Assert.Equal(MimeTypeCatalog.ImageJpeg, result.MediaType); // opaque → JPEG
    }

    [Fact]
    public void Oversized_by_bytes_is_shrunk_under_the_budget()
    {
        var src = GenerateImage(1400, 1400, SKEncodedImageFormat.Png);
        var options = new ImageNormalizationOptions { MaxBase64Bytes = 40_000 };

        var result = _normalizer.Normalize(src, options);

        Assert.Equal(ImageNormalizationOutcome.Normalized, result.Outcome);
        Assert.True(
            ImageDecodeMath.Base64Length(result.EncodedByteLength) <= options.MaxBase64Bytes,
            $"encoded base64 {ImageDecodeMath.Base64Length(result.EncodedByteLength)} exceeds budget");
    }

    [Fact]
    public void Image_within_bounds_is_passed_through_unchanged()
    {
        var src = GenerateImage(800, 600, SKEncodedImageFormat.Png);

        var result = _normalizer.Normalize(src, new ImageNormalizationOptions());

        Assert.Equal(ImageNormalizationOutcome.PassedThrough, result.Outcome);
        Assert.Equal(src.Length, result.Bytes!.Length); // not re-encoded
        var (w, h) = Dims(result.Bytes!);
        Assert.True(w <= 800 && h <= 600, "must not upscale");
    }

    [Fact]
    public void Image_with_alpha_stays_png_when_normalized()
    {
        var src = GenerateImage(3000, 3000, SKEncodedImageFormat.Png, withAlpha: true);

        var result = _normalizer.Normalize(src, new ImageNormalizationOptions());

        Assert.Equal(ImageNormalizationOutcome.Normalized, result.Outcome);
        Assert.Equal(MimeTypeCatalog.ImagePng, result.MediaType);
        Assert.True(Math.Max(Dims(result.Bytes!).W, Dims(result.Bytes!).H) <= 1568);
    }

    [Fact]
    public void Undecodable_input_is_dropped_with_a_reason()
    {
        var garbage = new byte[2048];
        new Random(7).NextBytes(garbage);

        var result = _normalizer.Normalize(garbage, new ImageNormalizationOptions());

        Assert.Equal(ImageNormalizationOutcome.Dropped, result.Outcome);
        Assert.Null(result.Bytes);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Empty_input_is_dropped()
    {
        var result = _normalizer.Normalize(ReadOnlySpan<byte>.Empty, new ImageNormalizationOptions());
        Assert.Equal(ImageNormalizationOutcome.Dropped, result.Outcome);
    }

    [Fact]
    public void Unshrinkable_image_is_dropped_not_shipped_raw()
    {
        // A budget no real JPEG can meet forces the shrink loop to exhaust and drop.
        var src = GenerateImage(2000, 2000, SKEncodedImageFormat.Png);
        var options = new ImageNormalizationOptions { MaxBase64Bytes = 500 };

        var result = _normalizer.Normalize(src, options);

        Assert.Equal(ImageNormalizationOutcome.Dropped, result.Outcome);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void Disabled_normalizer_passes_through_without_decoding()
    {
        var src = GenerateImage(4000, 3000, SKEncodedImageFormat.Png);

        var result = _normalizer.Normalize(src, new ImageNormalizationOptions { Enabled = false });

        Assert.Equal(ImageNormalizationOutcome.PassedThrough, result.Outcome);
        Assert.Equal(src.Length, result.Bytes!.Length);
    }

    [Fact]
    public void Large_jpeg_source_is_bounded_via_scaled_decode()
    {
        // 8000px long edge exercises the sample-size=4 scaled-decode path. Kept wide
        // (not square) so the test fixture itself stays modest while still proving the
        // oversized source is reduced to a bounded output.
        var src = GenerateImage(8000, 2000, SKEncodedImageFormat.Jpeg, quality: 92);

        var result = _normalizer.Normalize(src, new ImageNormalizationOptions());

        Assert.Equal(ImageNormalizationOutcome.Normalized, result.Outcome);
        var (w, h) = Dims(result.Bytes!);
        Assert.True(Math.Max(w, h) <= 1568, $"long edge {Math.Max(w, h)} exceeds cap");
    }

    private static (int W, int H) Dims(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return (codec.Info.Width, codec.Info.Height);
    }

    private static byte[] GenerateImage(
        int width, int height, SKEncodedImageFormat format, bool withAlpha = false, int quality = 90)
    {
        var alphaType = withAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, alphaType);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(withAlpha ? SKColors.Transparent : SKColors.White);
            using var paint = new SKPaint { IsAntialias = true };
            var rand = new Random(1234); // seeded → deterministic fixtures
            var maxRadius = Math.Max(6, width / 10);
            for (var i = 0; i < 400; i++)
            {
                paint.Color = new SKColor(
                    (byte)rand.Next(256), (byte)rand.Next(256), (byte)rand.Next(256),
                    withAlpha ? (byte)rand.Next(256) : (byte)255);
                canvas.DrawCircle(rand.Next(width), rand.Next(height), rand.Next(5, maxRadius), paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }
}
