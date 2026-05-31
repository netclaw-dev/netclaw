// -----------------------------------------------------------------------
// <copyright file="SessionMediaStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Protocol;

internal static class MediaMimeClassifier
{
    public static bool TryGetMediaModality(string? mimeType, out MediaModality modality)
    {
        var normalized = MimeTypeCatalog.Normalize(mimeType);
        if (normalized.StartsWith("image/", StringComparison.Ordinal))
        {
            modality = MediaModality.Image;
            return true;
        }

        if (normalized.StartsWith("audio/", StringComparison.Ordinal))
        {
            modality = MediaModality.Audio;
            return true;
        }

        if (normalized.StartsWith("video/", StringComparison.Ordinal))
        {
            modality = MediaModality.Video;
            return true;
        }

        modality = default;
        return false;
    }

    public static bool TryGetSupportedModelInput(
        string? mimeType,
        out MediaModality mediaModality,
        out ModelModality requiredModelModality)
    {
        if (MimeTypeCatalog.Normalize(mimeType) is
            MimeTypeCatalog.ImagePng or
            MimeTypeCatalog.ImageJpeg or
            MimeTypeCatalog.ImageGif or
            MimeTypeCatalog.ImageWebp)
        {
            mediaModality = MediaModality.Image;
            requiredModelModality = ModelModality.Image;
            return true;
        }

        mediaModality = default;
        requiredModelModality = default;
        return false;
    }
}

internal static class SessionMediaStore
{
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

        var mimeType = MimeTypeCatalog.Normalize(data.MediaType);
        if (!MediaMimeClassifier.TryGetMediaModality(mimeType, out var modality))
            return null;

        var mediaDir = GetOrCreateMediaDirectory(sessionDir);
        var fileName = CreateMediaFileName(mimeType);
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);

        return CreateReference(fileName, mimeType, modality, bytes.Length);
    }

    public static SerializableMediaReference CopyFile(
        string sourcePath,
        string sessionDir,
        string mimeType,
        MediaModality modality,
        long fileSizeBytes)
    {
        var normalizedMime = MimeTypeCatalog.Normalize(mimeType);
        var mediaDir = GetOrCreateMediaDirectory(sessionDir);
        var fileName = CreateMediaFileName(normalizedMime);
        File.Copy(sourcePath, Path.Combine(mediaDir, fileName));

        return CreateReference(fileName, normalizedMime, modality, fileSizeBytes);
    }

    private static string CreateMediaFileName(string mimeType)
        => $"{Guid.NewGuid():N}{MimeTypeCatalog.ExtensionFor(mimeType)}";

    private static SerializableMediaReference CreateReference(
        string fileName,
        string mimeType,
        MediaModality modality,
        long fileSizeBytes)
        => new()
        {
            RelativePath = fileName,
            MimeType = new MimeType(mimeType),
            Modality = (int)modality,
            FileSizeBytes = fileSizeBytes
        };
}
