// -----------------------------------------------------------------------
// <copyright file="SkiaImageNormalizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SkiaSharp;

namespace Netclaw.Media;

/// <summary>
/// SkiaSharp-backed <see cref="IImageNormalizer"/>. Uses scaled decode (JPEG DCT /
/// WebP) so an oversized source is never fully materialized; for formats the codec
/// cannot scale on load (PNG/GIF/BMP) it enforces a fail-loud decode ceiling rather
/// than risk an OOM. Opaque images re-encode to JPEG; images with alpha stay PNG.
/// </summary>
public sealed class SkiaImageNormalizer : IImageNormalizer
{
    /// <summary>
    /// Hard ceiling on the decoded bitmap (width*height*4 bytes) for codecs that cannot
    /// scale on load. Bounds peak memory for pathological PNGs; such images are dropped
    /// with guidance rather than decoded. 256 MiB ≈ an 8192x8192 RGBA bitmap.
    /// </summary>
    private const long MaxDecodeBytes = 256L * 1024 * 1024;

    /// <summary>Maximum iterative size-shrink steps when chasing the byte budget before giving up.</summary>
    private const int MaxShrinkSteps = 8;

    /// <summary>JPEG quality ladder tried at each size before shrinking dimensions.</summary>
    private static readonly int[] JpegQualityLadder = [70, 55, 40];

    private static readonly SKSamplingOptions DownscaleSampling = new(SKCubicResampler.Mitchell);

    public ImageNormalizationResult Normalize(ReadOnlySpan<byte> source, ImageNormalizationOptions options)
    {
        if (source.IsEmpty)
            return ImageNormalizationResult.Drop("empty image data");

        if (!options.Enabled)
            return PassThrough(source.ToArray());

        using var data = SKData.CreateCopy(source.ToArray());
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return ImageNormalizationResult.Drop("not a decodable image");

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            return ImageNormalizationResult.Drop("image has no pixels");

        var format = codec.EncodedFormat;
        // Alpha must be read from the SOURCE codec: a decoded SKBitmap defaults to
        // Premul regardless of whether the source actually had transparency, so
        // inspecting the working bitmap would force every image to PNG.
        var sourceIsOpaque = info.AlphaType == SKAlphaType.Opaque;
        var withinLongEdge = Math.Max(info.Width, info.Height) <= options.MaxLongEdgePixels;
        var withinBudget = ImageDecodeMath.Base64Length(source.Length) <= options.MaxBase64Bytes;
        var supportedPassthrough = format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp;

        // Already small and a format the provider accepts inline: keep the original bytes,
        // no decode, no re-encode.
        if (withinLongEdge && withinBudget && supportedPassthrough)
            return PassThrough(source.ToArray(), info.Width, info.Height, format);

        // Pick a scaled-decode size. For JPEG/WebP this shrinks the decode itself; for
        // PNG the codec returns full dimensions, which the ceiling below guards.
        var sampleSize = ImageDecodeMath.ChooseDecodeSampleSize(info.Width, info.Height, options.MaxLongEdgePixels);
        var scaled = codec.GetScaledDimensions(1f / sampleSize);
        if ((long)scaled.Width * scaled.Height * 4 > MaxDecodeBytes)
            return ImageNormalizationResult.Drop(
                $"image too large to bound safely in memory ({info.Width}x{info.Height}); re-save smaller or as JPEG");

        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height);
        using var decoded = SKBitmap.Decode(codec, decodeInfo);
        if (decoded is null)
            return ImageNormalizationResult.Drop("image could not be decoded");

        // Precise resize down to the long-edge cap (the scaled decode lands at-or-above it).
        using var capped = ResizeToLongEdge(decoded, options.MaxLongEdgePixels);
        var working = capped ?? decoded;

        // Opaque images re-encode to JPEG (big size win); images with transparency
        // stay PNG so alpha survives.
        return EncodeWithinBudget(working, useJpeg: sourceIsOpaque, options);
    }

    private static ImageNormalizationResult EncodeWithinBudget(
        SKBitmap bitmap, bool useJpeg, ImageNormalizationOptions options)
    {
        var targetFormat = useJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        var mediaType = useJpeg ? MimeTypeCatalog.ImageJpeg : MimeTypeCatalog.ImagePng;

        // At each size try the configured quality, then a descending JPEG ladder, before
        // shrinking dimensions. PNG is lossless so the ladder collapses to a single pass.
        var qualities = useJpeg
            ? new[] { options.JpegQuality }.Concat(JpegQualityLadder).ToArray()
            : [options.JpegQuality];

        var current = bitmap;
        SKBitmap? owned = null;

        try
        {
            for (var step = 0; step < MaxShrinkSteps; step++)
            {
                foreach (var quality in qualities)
                {
                    var bytes = Encode(current, targetFormat, quality);
                    if (bytes is null)
                        return ImageNormalizationResult.Drop("image could not be re-encoded");

                    if (ImageDecodeMath.Base64Length(bytes.Length) <= options.MaxBase64Bytes)
                    {
                        return new ImageNormalizationResult
                        {
                            Outcome = ImageNormalizationOutcome.Normalized,
                            Bytes = bytes,
                            Width = current.Width,
                            Height = current.Height,
                            EncodedByteLength = bytes.Length,
                            MediaType = mediaType
                        };
                    }

                    if (!useJpeg)
                        break; // PNG: quality is inert, go straight to a size reduction
                }

                // Still over budget at this size: shrink ~20% on each edge and retry.
                var nextW = (int)(current.Width * 0.8);
                var nextH = (int)(current.Height * 0.8);
                if (nextW < 1 || nextH < 1)
                    break;

                var next = current.Resize(new SKImageInfo(nextW, nextH), DownscaleSampling);
                owned?.Dispose();
                owned = next;
                if (next is null)
                    break;
                current = next;
            }

            return ImageNormalizationResult.Drop(
                $"image could not be reduced under the {options.MaxBase64Bytes / (1024 * 1024)}MB budget");
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static SKBitmap? ResizeToLongEdge(SKBitmap source, int maxLongEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxLongEdge)
            return null; // already within cap; caller uses the source as-is

        var scale = (double)maxLongEdge / longEdge;
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        return source.Resize(new SKImageInfo(w, h), DownscaleSampling);
    }

    private static byte[]? Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, quality);
        return encoded?.ToArray();
    }

    private static ImageNormalizationResult PassThrough(
        byte[] bytes, int width = 0, int height = 0, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
        => new()
        {
            Outcome = ImageNormalizationOutcome.PassedThrough,
            Bytes = bytes,
            Width = width,
            Height = height,
            EncodedByteLength = bytes.Length,
            MediaType = format switch
            {
                SKEncodedImageFormat.Jpeg => MimeTypeCatalog.ImageJpeg,
                SKEncodedImageFormat.Webp => MimeTypeCatalog.ImageWebp,
                _ => MimeTypeCatalog.ImagePng
            }
        };
}
