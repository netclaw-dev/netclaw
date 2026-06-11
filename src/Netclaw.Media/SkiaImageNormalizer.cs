// -----------------------------------------------------------------------
// <copyright file="SkiaImageNormalizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SkiaSharp;

namespace Netclaw.Media;

/// <summary>
/// SkiaSharp-backed <see cref="IImageNormalizer"/>. It ONLY resizes: an image whose
/// longest edge exceeds the cap is scaled down and re-encoded in its <b>original</b>
/// container format — a PNG stays a PNG, a JPEG stays a JPEG. It never transcodes
/// (e.g. PNG→JPEG) or quality-reduces, so screenshots/diagrams keep their fidelity.
/// Scaled decode (JPEG DCT / WebP) keeps an oversized source from being fully
/// materialized; a hard decode ceiling drops pathological inputs rather than risk an
/// OOM. Formats Skia cannot re-encode (GIF) and images that still exceed the payload
/// budget after resizing are dropped (fail-loud), never silently degraded.
/// </summary>
public sealed class SkiaImageNormalizer : IImageNormalizer
{
    /// <summary>
    /// Hard ceiling on the decoded bitmap (width*height*4 bytes) for codecs that cannot
    /// scale on load (PNG). Bounds peak memory for pathological PNGs; such images are
    /// dropped rather than decoded. 256 MiB ≈ an 8192x8192 RGBA bitmap.
    /// </summary>
    private const long MaxDecodeBytes = 256L * 1024 * 1024;

    private static readonly SKSamplingOptions DownscaleSampling = new(SKCubicResampler.Mitchell);

    public ImageNormalizationResult Normalize(ReadOnlySpan<byte> source, ImageNormalizationOptions options)
    {
        if (source.IsEmpty)
            return ImageNormalizationResult.Drop("empty image data");

        // Rollback path: pass the bytes through without touching SkiaSharp. MediaType is
        // left null so the caller keeps its own declared MIME (we never inspected the bytes).
        if (!options.Enabled)
            return Bypass(source.ToArray());

        try
        {
            using var data = SKData.CreateCopy(source);
            using var codec = SKCodec.Create(data);
            if (codec is null)
                // Skia couldn't parse the bytes as any supported image container: a corrupt
                // or truncated file, non-image bytes carrying an image MIME, or a format Skia
                // doesn't decode (e.g. HEIC/AVIF without a platform codec, SVG). JPEG/PNG/WebP/
                // GIF/BMP all decode fine. Fail loud rather than ship un-vetted bytes.
                return ImageNormalizationResult.Drop("not a decodable image");

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
                return ImageNormalizationResult.Drop("image has no pixels");

            var format = codec.EncodedFormat;
            var withinLongEdge = Math.Max(info.Width, info.Height) <= options.MaxLongEdge.Value;
            var withinBudget = ImageDecodeMath.Base64Length(source.Length) <= options.MaxBase64.Value;

            // We only ever RESIZE — never transcode or quality-reduce. Skia can re-encode
            // JPEG/PNG/WebP; a format it cannot encode (GIF) is left untouched.
            var canResize = format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp;

            if (!canResize || withinLongEdge)
            {
                // Nothing to resize (already within the dimension cap, or a format we won't
                // decode). Keep the original bytes if they fit the payload budget; otherwise
                // drop loudly — we won't transcode or further shrink to force a fit.
                return withinBudget
                    ? PassThrough(source.ToArray(), info.Width, info.Height, format)
                    : ImageNormalizationResult.Drop(
                        $"image exceeds the {options.MaxBase64} payload budget and cannot be bounded by resizing");
            }

            // Over the dimension cap: scaled-decode (bounding peak memory), then resize to the cap.
            var sampleSize = ImageDecodeMath.ChooseDecodeSampleSize(info.Width, info.Height, options.MaxLongEdge.Value);
            var scaled = codec.GetScaledDimensions(1f / sampleSize);
            if ((long)scaled.Width * scaled.Height * 4 > MaxDecodeBytes)
                return ImageNormalizationResult.Drop(
                    $"image too large to bound safely in memory ({info.Width}x{info.Height})");

            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(scaled.Width, scaled.Height));
            if (decoded is null)
                return ImageNormalizationResult.Drop("image could not be decoded");

            using var capped = ResizeToLongEdge(decoded, options.MaxLongEdge.Value);
            var working = capped ?? decoded;

            var bytes = Encode(working, format, options.JpegQuality.Value);
            if (bytes is null)
                return ImageNormalizationResult.Drop("image could not be re-encoded");

            // A resized image that still blows the budget is dropped, not shrunk further.
            // If this drop becomes common in practice, the place to reintroduce a bounded
            // iterative size-reduction loop is right here (we deliberately kept it "just
            // resize" for now — see PR #1345 review).
            if (ImageDecodeMath.Base64Length(bytes.Length) > options.MaxBase64.Value)
                return ImageNormalizationResult.Drop(
                    $"image still exceeds the {options.MaxBase64} payload budget after resizing");

            return new ImageNormalizationResult
            {
                Outcome = ImageNormalizationOutcome.Normalized,
                Bytes = bytes,
                Width = new Pixels(working.Width),
                Height = new Pixels(working.Height),
                EncodedSize = new ByteSize(bytes.Length),
                MediaType = MediaTypeFor(format)
            };
        }
        catch (Exception ex)
        {
            // SkiaSharp throws on native-load failure (DllNotFoundException), native OOM
            // during decode/resize/encode, or a malformed-but-openable codec. The contract
            // is fail-loud-as-Dropped, never throw — every caller depends on that.
            return ImageNormalizationResult.Drop($"image processing failed ({ex.GetType().Name})");
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
        byte[] bytes, int width, int height, SKEncodedImageFormat format)
        => new()
        {
            Outcome = ImageNormalizationOutcome.PassedThrough,
            Bytes = bytes,
            Width = new Pixels(width),
            Height = new Pixels(height),
            EncodedSize = new ByteSize(bytes.Length),
            MediaType = MediaTypeFor(format)
        };

    private static ImageNormalizationResult Bypass(byte[] bytes)
        => new()
        {
            Outcome = ImageNormalizationOutcome.PassedThrough,
            Bytes = bytes,
            EncodedSize = new ByteSize(bytes.Length),
            MediaType = null
        };

    private static string MediaTypeFor(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => MimeTypeCatalog.ImageJpeg,
        SKEncodedImageFormat.Webp => MimeTypeCatalog.ImageWebp,
        SKEncodedImageFormat.Gif => MimeTypeCatalog.ImageGif,
        _ => MimeTypeCatalog.ImagePng
    };
}
