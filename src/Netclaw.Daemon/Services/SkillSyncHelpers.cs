using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Daemon.Services;

internal static class SkillSyncHelpers
{
    internal static readonly string[] AllowedResourcePrefixes = ["references", "scripts", "assets"];

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    internal static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    internal static string? ValidateResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
            return null;

        var normalized = path.Replace('\\', '/');
        var firstSegment = normalized.Split('/')[0];
        if (!AllowedResourcePrefixes.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            return null;

        return normalized;
    }

    internal static SkillSyncState ReadSyncState(string path, ILogger logger)
    {
        if (!File.Exists(path))
            return new SkillSyncState();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SkillSyncState>(json) ?? new SkillSyncState();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read sync state at {Path} — starting fresh", path);
            return new SkillSyncState();
        }
    }

    internal static void WriteSyncState(string path, SkillSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, IndentedJsonOptions);
        File.WriteAllText(path, json);
    }

    internal static async Task ReplaceSkillDirectoryAsync(
        string parentDirectory,
        string skillName,
        IReadOnlyList<DownloadedSkillFile> files,
        CancellationToken cancellationToken)
    {
        var skillDir = Path.Combine(parentDirectory, skillName);
        var stagingRoot = Path.Combine(parentDirectory, ".staging");
        Directory.CreateDirectory(stagingRoot);

        var stagingDir = Path.Combine(stagingRoot, $"{skillName}-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(stagingRoot, $"{skillName}-backup-{Guid.NewGuid():N}");

        Directory.CreateDirectory(stagingDir);

        try
        {
            foreach (var file in files)
            {
                var targetPath = Path.Combine(stagingDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(targetPath, file.Content, cancellationToken);
            }

            if (Directory.Exists(skillDir))
                Directory.Move(skillDir, backupDir);

            Directory.Move(stagingDir, skillDir);

            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(skillDir) && Directory.Exists(backupDir))
                Directory.Move(backupDir, skillDir);

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);

            if (Directory.Exists(backupDir) && !Directory.Exists(skillDir))
                Directory.Delete(backupDir, recursive: true);
        }
    }
}

internal sealed record DownloadedSkillFile(string RelativePath, string Content);
