// -----------------------------------------------------------------------
// <copyright file="SessionMediaStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Media;

namespace Netclaw.Actors.Protocol;

internal static class SessionMediaStore
{
    // The media store is the single normalization chokepoint: every image bound for
    // a model is written here exactly once (WriteDataContent / CopyFile) and read
    // back on every turn, so bounding it here bounds it everywhere (closes #1296).
    // The normalizer is stateless and thread-safe, so a shared instance avoids
    // plumbing an IImageNormalizer through every static call site. Bounds are fixed
    // constants (no runtime config that could re-open the OOM).
    private static readonly IImageNormalizer ImageNormalizer = new SkiaImageNormalizer();
    private static readonly ImageNormalizationOptions ImageOptions = new();


    public static bool TryGetMediaModality(string? mimeType, out MediaModality modality)
        => TryGetMediaModality(new MimeType(mimeType), out modality);

    public static bool TryGetMediaModality(MimeType mimeType, out MediaModality modality)
    {
        switch (MimeTypeCatalog.GetMediaKind(mimeType))
        {
            case MediaKind.Image:
                modality = MediaModality.Image;
                return true;
            case MediaKind.Audio:
                modality = MediaModality.Audio;
                return true;
            case MediaKind.Video:
                modality = MediaModality.Video;
                return true;
            default:
                modality = default;
                return false;
        }
    }

    public static bool TryGetSupportedModelInput(
        MimeType mimeType,
        out MediaModality mediaModality,
        out ModelModality requiredModelModality)
    {
        if (MimeTypeCatalog.IsModelInputSupported(mimeType))
        {
            mediaModality = MediaModality.Image;
            requiredModelModality = ModelModality.Image;
            return true;
        }

        mediaModality = default;
        requiredModelModality = default;
        return false;
    }

    public static string GetMediaPath(string sessionDir, string relativePath)
        => Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory, relativePath);

    public static string GetOrCreateMediaDirectory(string sessionDir)
    {
        var mediaDir = Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory);
        Directory.CreateDirectory(mediaDir);
        return mediaDir;
    }

    public static SerializableMediaReference? WriteDataContent(DataContent data, string sessionDir)
    {
        var bytes = data.Data.ToArray();
        if (bytes.Length == 0)
            return null;

        var mimeType = new MimeType(data.MediaType);
        if (!TryGetMediaModality(mimeType, out var modality))
            return null;

        if (NormalizeImage(ref bytes, ref mimeType, modality) == ImageGate.Dropped)
            return null; // image could not be bounded; caller surfaces the omission

        var mediaDir = GetOrCreateMediaDirectory(sessionDir);
        var fileName = CreateMediaFileName(mimeType);
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);

        return CreateReference(fileName, mimeType, modality, bytes.Length);
    }

    /// <summary>
    /// Copies a model-input file into session media, normalizing images at the
    /// boundary. Returns <c>null</c> when an image cannot be bounded — the caller
    /// counts the omission toward the model-input handoff warning.
    /// </summary>
    public static SerializableMediaReference? CopyFile(
        string sourcePath,
        string sessionDir,
        MimeType mimeType,
        MediaModality modality,
        long fileSizeBytes)
    {
        var mediaDir = GetOrCreateMediaDirectory(sessionDir);

        // Non-image media streams through with a plain copy — no decode, no buffer.
        if (modality != MediaModality.Image)
        {
            var copyName = CreateMediaFileName(mimeType);
            File.Copy(sourcePath, Path.Combine(mediaDir, copyName));
            return CreateReference(copyName, mimeType, modality, fileSizeBytes);
        }

        var bytes = File.ReadAllBytes(sourcePath);
        if (NormalizeImage(ref bytes, ref mimeType, modality) == ImageGate.Dropped)
            return null;

        var fileName = CreateMediaFileName(mimeType);
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);
        return CreateReference(fileName, mimeType, modality, bytes.Length);
    }

    private enum ImageGate { Kept, Dropped }

    /// <summary>
    /// Bounds an image in place before it is persisted. Non-image media is left
    /// untouched. On a normalized result the bytes and MIME are replaced with the
    /// bounded artifact (PNG may become JPEG); on a drop the image is refused.
    /// </summary>
    private static ImageGate NormalizeImage(ref byte[] bytes, ref MimeType mimeType, MediaModality modality)
    {
        if (modality != MediaModality.Image)
            return ImageGate.Kept;

        var result = ImageNormalizer.Normalize(bytes, ImageOptions);
        if (result.Outcome == ImageNormalizationOutcome.Dropped)
            return ImageGate.Dropped;

        bytes = result.Bytes!;
        mimeType = new MimeType(result.MediaType!);
        return ImageGate.Kept;
    }

    private static string CreateMediaFileName(MimeType mimeType)
        => $"{Guid.NewGuid():N}{MimeTypeCatalog.ExtensionFor(mimeType)}";

    private static SerializableMediaReference CreateReference(
        string fileName,
        MimeType mimeType,
        MediaModality modality,
        long fileSizeBytes)
        => new()
        {
            RelativePath = fileName,
            MimeType = mimeType,
            Modality = (int)modality,
            FileSizeBytes = fileSizeBytes
        };
}
