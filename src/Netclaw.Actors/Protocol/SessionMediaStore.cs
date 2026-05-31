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

        var mediaDir = GetOrCreateMediaDirectory(sessionDir);
        var fileName = CreateMediaFileName(mimeType);
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), bytes);

        return CreateReference(fileName, mimeType, modality, bytes.Length);
    }

    public static SerializableMediaReference CopyFile(
        string sourcePath,
        string sessionDir,
        MimeType mimeType,
        MediaModality modality,
        long fileSizeBytes)
    {
        var mediaDir = GetOrCreateMediaDirectory(sessionDir);
        var fileName = CreateMediaFileName(mimeType);
        File.Copy(sourcePath, Path.Combine(mediaDir, fileName));

        return CreateReference(fileName, mimeType, modality, fileSizeBytes);
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
