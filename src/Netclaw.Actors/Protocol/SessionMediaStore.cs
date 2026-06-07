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

    public static MediaWriteResult WriteDataContent(DataContent data, string sessionDir)
    {
        var bytes = data.Data.ToArray();
        if (bytes.Length == 0)
            return MediaWriteResult.Skipped;

        var mimeType = new MimeType(data.MediaType);
        if (!TryGetMediaModality(mimeType, out var modality))
            return MediaWriteResult.Skipped;

        var (dropped, reason) = NormalizeImage(ref bytes, ref mimeType);
        if (dropped)
            return MediaWriteResult.Drop(reason); // caller surfaces the omission note

        var mediaDir = GetOrCreateMediaDirectory(sessionDir);
        var fileName = CreateMediaFileName(mimeType);
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);

        return MediaWriteResult.Written(CreateReference(fileName, mimeType, modality, bytes.Length));
    }

    /// <summary>
    /// Appends a visible <c>[image omitted: reason]</c> note to a message's content
    /// so a dropped image is surfaced to the model rather than vanishing silently.
    /// </summary>
    public static string AppendOmittedImageNote(string content, string reason)
    {
        var note = $"[image omitted: {reason}]";
        return string.IsNullOrEmpty(content) ? note : content + "\n" + note;
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
        // file_read drops surface via the model-input handoff warning (the
        // RequestedCount > MediaReferences.Count gap), so the per-image reason
        // is not needed here.
        if (NormalizeImage(ref bytes, ref mimeType).Dropped)
            return null;

        var fileName = CreateMediaFileName(mimeType);
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);
        return CreateReference(fileName, mimeType, modality, bytes.Length);
    }

    /// <summary>
    /// Resizes an oversized image in place before it is persisted. Only model-input
    /// images (the ones base64-inlined into a request, hence the #1296 OOM surface) are
    /// bounded — non-model-input media (audio/video, and bmp/tiff that the model can't
    /// ingest) is left byte-for-byte. On a resize the bytes are replaced and the MIME is
    /// updated only if the normalizer reports one (the rollback/bypass path leaves the
    /// declared MIME intact). On a drop, returns the reason for the omission note.
    /// </summary>
    private static (bool Dropped, string? Reason) NormalizeImage(ref byte[] bytes, ref MimeType mimeType)
    {
        if (!MimeTypeCatalog.IsModelInputSupported(mimeType))
            return (false, null);

        var result = ImageNormalizer.Normalize(bytes, ImageOptions);
        if (result.Outcome == ImageNormalizationOutcome.Dropped)
            return (true, result.Reason);

        bytes = result.Bytes!;
        if (result.MediaType is not null)
            mimeType = new MimeType(result.MediaType);
        return (false, null);
    }

    /// <summary>
    /// Outcome of a media write: a written reference, a silent skip (non-media /
    /// empty), or a drop carrying the reason for the omission note.
    /// </summary>
    public readonly record struct MediaWriteResult(SerializableMediaReference? Reference, string? DroppedReason)
    {
        /// <summary>Non-media or empty content — nothing written, no note.</summary>
        public static MediaWriteResult Skipped => default;

        public static MediaWriteResult Written(SerializableMediaReference reference) => new(reference, null);

        public static MediaWriteResult Drop(string? reason)
            => new(null, reason ?? "image could not be processed");
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
