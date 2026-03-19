using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class SessionScopedFileAccess
{
    public static bool TryResolveAuthorizedPath(
        string rawPath,
        ToolExecutionContext context,
        out string fullPath,
        out string error)
    {
        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            error = $"Error: Invalid path: {ex.Message}";
            return false;
        }

        if (ResolveAudience(context) != TrustAudience.Public)
        {
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(context.SessionDirectory))
        {
            error = "Error: Public trust context may only access files inside the current session directory, but no session directory is available.";
            return false;
        }

        var sessionDirectory = NormalizeDirectoryPath(context.SessionDirectory);
        if (!IsPathWithinDirectory(fullPath, sessionDirectory))
        {
            error = $"Error: Public trust context may only access files inside the current session directory ({sessionDirectory}).";
            return false;
        }

        if (ContainsSymlinkSegment(sessionDirectory, fullPath))
        {
            error = $"Error: Public trust context may not access files through symlinked paths inside the current session directory ({sessionDirectory}).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static TrustAudience ResolveAudience(ToolExecutionContext context)
        => SecurityPolicyDefaults.TryParseAudience(context.Audience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context.SessionId);

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        return Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            return false;

        if (fullPath.Length == directory.Length)
            return true;

        var boundary = fullPath[directory.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static bool ContainsSymlinkSegment(string sessionDirectory, string fullPath)
    {
        var relativePath = Path.GetRelativePath(sessionDirectory, fullPath);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
            return false;

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var currentPath = sessionDirectory;

        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
                continue;

            try
            {
                var attributes = File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
