// -----------------------------------------------------------------------
// <copyright file="HistoricalAttachmentInbox.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Actors.Protocol;
using Netclaw.Security;

namespace Netclaw.Channels;

public static class HistoricalAttachmentInbox
{
    public static bool TryGetExistingFile(
        string inboxDir,
        string rawFilename,
        string sourceKey,
        out string existingPath,
        out long existingSize)
    {
        existingPath = BuildStablePath(inboxDir, rawFilename, sourceKey);
        existingSize = 0;

        if (!File.Exists(existingPath))
            return false;

        var info = new FileInfo(existingPath);
        if (info.Length <= 0)
            return false;

        existingSize = info.Length;
        return true;
    }

    public static string PromoteOrReuse(
        string inboxDir,
        string rawFilename,
        string sourceKey,
        string stagedFilePath)
    {
        var targetPath = BuildStablePath(inboxDir, rawFilename, sourceKey);
        if (File.Exists(targetPath))
        {
            File.Delete(stagedFilePath);
            return targetPath;
        }

        try
        {
            File.Move(stagedFilePath, targetPath);
            return targetPath;
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            File.Delete(stagedFilePath);
            return targetPath;
        }
    }

    private static string BuildStablePath(string inboxDir, string rawFilename, string sourceKey)
    {
        var safeName = FilenameSanitizer.Sanitize(rawFilename);
        var nameOnly = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        var stableSuffix = ComputeStableSuffix(sourceKey);
        return Path.Combine(inboxDir, $"{nameOnly}_hist_{stableSuffix}{extension}");
    }

    private static string ComputeStableSuffix(string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }
}
