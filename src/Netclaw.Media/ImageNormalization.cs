// -----------------------------------------------------------------------
// <copyright file="ImageNormalization.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Media;

/// <summary>
/// Outcome of running an image through <see cref="IImageNormalizer"/>.
/// </summary>
public enum ImageNormalizationOutcome
{
    /// <summary>The image was decoded and downscaled and/or re-encoded to fit the caps.</summary>
    Normalized,

    /// <summary>The source was already within caps and a supported format; original bytes are returned unchanged.</summary>
    PassedThrough,

    /// <summary>The image could not be decoded, or could not be reduced under the byte budget. No bytes are returned.</summary>
    Dropped
}

/// <summary>
/// Bounds applied when preparing an image for model input. Surfaced as configuration
/// (see <c>Netclaw.Configuration</c>); defaults match Anthropic's documented vision
/// limits (≈1568px long edge, 5MB on-the-wire).
/// </summary>
public sealed record ImageNormalizationOptions
{
    /// <summary>Longest output edge in pixels. Anthropic downscales above ~1568px server-side anyway.</summary>
    public int MaxLongEdgePixels { get; init; } = 1568;

    /// <summary>
    /// Budget on the base64-encoded payload size. The wire form of an image is base64,
    /// so this is the quantity that actually bounds request size and heap. Default 5MB
    /// matches Anthropic's hard API limit.
    /// </summary>
    public int MaxBase64Bytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>JPEG quality (1-100) used when re-encoding opaque images.</summary>
    public int JpegQuality { get; init; } = 85;

    /// <summary>When false, the normalizer passes every image through untouched (rollback switch).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Result of normalizing one image. <see cref="Bytes"/> is non-null only for
/// <see cref="ImageNormalizationOutcome.Normalized"/> and
/// <see cref="ImageNormalizationOutcome.PassedThrough"/>; on
/// <see cref="ImageNormalizationOutcome.Dropped"/> the caller MUST attach no image
/// and surface <see cref="Reason"/> as a visible note (fail-loud, never raw passthrough).
/// </summary>
public sealed record ImageNormalizationResult
{
    public required ImageNormalizationOutcome Outcome { get; init; }
    public byte[]? Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int EncodedByteLength { get; init; }
    public string? MediaType { get; init; }
    public string? Reason { get; init; }

    public static ImageNormalizationResult Drop(string reason)
        => new() { Outcome = ImageNormalizationOutcome.Dropped, Reason = reason };
}

/// <summary>
/// Prepares an image for model input by downscaling it to a bounded payload. The
/// implementation MUST NOT throw on undecodable or oversized input — it returns a
/// <see cref="ImageNormalizationOutcome.Dropped"/> result instead.
/// </summary>
public interface IImageNormalizer
{
    ImageNormalizationResult Normalize(ReadOnlySpan<byte> source, ImageNormalizationOptions options);
}

/// <summary>
/// Pure (no native-codec) helpers for the decode bound. Separated so the memory-ceiling
/// math is unit-testable without loading SkiaSharp's native library.
/// </summary>
public static class ImageDecodeMath
{
    /// <summary>
    /// Largest power-of-two decode sample size (1,2,4,8) such that the source's longest
    /// edge, divided by the sample size, is still no smaller than <paramref name="maxLongEdgePixels"/>.
    /// Codecs with scaled decode (JPEG DCT, WebP) use this to avoid ever materializing the
    /// full-resolution bitmap; a final precise resize then lands exactly on the cap. Capped
    /// at 8 because that is the JPEG DCT denominator limit.
    /// </summary>
    public static int ChooseDecodeSampleSize(int sourceWidth, int sourceHeight, int maxLongEdgePixels)
    {
        if (maxLongEdgePixels <= 0)
            return 1;

        var longEdge = Math.Max(sourceWidth, sourceHeight);
        if (longEdge <= maxLongEdgePixels)
            return 1;

        var sampleSize = 1;
        while (sampleSize < 8 && longEdge / (sampleSize * 2) >= maxLongEdgePixels)
            sampleSize *= 2;

        return sampleSize;
    }

    /// <summary>Length of the base64 encoding of <paramref name="byteCount"/> raw bytes (no line breaks).</summary>
    public static long Base64Length(long byteCount) => (byteCount + 2) / 3 * 4;
}
