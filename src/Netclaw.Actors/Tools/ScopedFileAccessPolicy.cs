using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ScopedFileAccessPolicy
{
    private readonly ToolAudienceProfileResolver _profileResolver;

    public ScopedFileAccessPolicy(ToolConfig toolConfig)
    {
        _profileResolver = new ToolAudienceProfileResolver(toolConfig);
    }

    public bool TryResolveReadPath(string rawPath, ToolExecutionContext context, out string fullPath, out string error)
        => TryResolvePath(rawPath, context, AccessKind.Read, out fullPath, out error);

    public bool TryResolveWritePath(string rawPath, ToolExecutionContext context, out string fullPath, out string error)
        => TryResolvePath(rawPath, context, AccessKind.Write, out fullPath, out error);

    public bool TryResolveAttachPath(string rawPath, ToolExecutionContext context, out string fullPath, out string error)
        => TryResolvePath(rawPath, context, AccessKind.Attach, out fullPath, out error);

    public IReadOnlyList<string> GetRootsForContext(ToolExecutionContext context, AccessKind accessKind)
    {
        var profile = _profileResolver.ResolveProfile(context);
        var access = accessKind switch
        {
            AccessKind.Read => profile.ReadFiles,
            AccessKind.Write => profile.WriteFiles,
            AccessKind.Attach => profile.AttachFiles,
            _ => profile.ReadFiles
        };

        return _profileResolver.ResolveRoots(access, context)
            .Select(NormalizeDirectoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryResolvePath(
        string rawPath,
        ToolExecutionContext context,
        AccessKind accessKind,
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

        var profile = _profileResolver.ResolveProfile(context);
        var access = accessKind switch
        {
            AccessKind.Read => profile.ReadFiles,
            AccessKind.Write => profile.WriteFiles,
            AccessKind.Attach => profile.AttachFiles,
            _ => profile.ReadFiles
        };

        if (access.Mode == ToolFilesystemMode.All)
        {
            error = string.Empty;
            return true;
        }

        if (access.Mode == ToolFilesystemMode.None)
        {
            error = $"Error: {GetAudienceLabel(context)} trust context does not allow {accessKind.ToString().ToLowerInvariant()} access to local files.";
            return false;
        }

        var roots = _profileResolver.ResolveRoots(access, context)
            .Select(NormalizeDirectoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (roots.Length == 0)
        {
            error = $"Error: {GetAudienceLabel(context)} trust context does not have any configured local file roots for {accessKind.ToString().ToLowerInvariant()} access.";
            return false;
        }

        foreach (var root in roots)
        {
            if (!IsPathWithinDirectory(fullPath, root))
                continue;

            if (ContainsSymlinkSegment(root, fullPath))
            {
                error = $"Error: {GetAudienceLabel(context)} trust context may not access files through symlinked paths inside the current session directory or configured roots.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = $"Error: {GetAudienceLabel(context)} trust context may only access files inside the current session directory or configured roots: {string.Join(", ", roots)}.";
        return false;
    }

    private static string GetAudienceLabel(ToolExecutionContext context)
    {
        var audience = SecurityPolicyDefaults.TryParseAudience(context.Audience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context.SessionId);

        return audience switch
        {
            TrustAudience.Public => "Public",
            TrustAudience.Team => "Team",
            TrustAudience.Personal => "Personal",
            _ => "Public"
        };
    }

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

    private static bool ContainsSymlinkSegment(string allowedRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(allowedRoot, fullPath);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
            return false;

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var currentPath = allowedRoot;

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

    internal enum AccessKind
    {
        Read,
        Write,
        Attach
    }
}
