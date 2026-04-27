using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ScopedFileAccessPolicy
{
    private readonly ToolAudienceProfileResolver _profileResolver;
    private readonly Lazy<IReadOnlyList<string>> _cachedGlobalReadRoots;

    public ScopedFileAccessPolicy(ToolConfig toolConfig, NetclawPaths? paths = null)
    {
        _profileResolver = new ToolAudienceProfileResolver(toolConfig, paths);
        _cachedGlobalReadRoots = new Lazy<IReadOnlyList<string>>(() =>
            _profileResolver.ResolveGlobalReadRoots()
                .Select(NormalizeDirectoryPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
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
        var access = GetAccessProfile(profile, accessKind);
        return ResolveAndMergeRoots(access, context, ResolveAudience(context), accessKind);
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
        var access = GetAccessProfile(profile, accessKind);

        if (access.Mode == ToolFilesystemMode.All)
        {
            error = string.Empty;
            return true;
        }

        var audience = ResolveAudience(context);
        var label = GetAudienceLabel(audience);

        if (access.Mode == ToolFilesystemMode.None)
        {
            error = $"Error: {label} trust context does not allow {accessKind.ToString().ToLowerInvariant()} access to local files.";
            return false;
        }

        var roots = ResolveAndMergeRoots(access, context, audience, accessKind);

        if (roots.Count == 0)
        {
            error = $"Error: {label} trust context does not have any configured local file roots for {accessKind.ToString().ToLowerInvariant()} access.";
            return false;
        }

        foreach (var root in roots)
        {
            if (!IsPathWithinDirectory(fullPath, root))
                continue;

            if (ContainsSymlinkSegment(root, fullPath))
            {
                error = $"Error: {label} trust context may not access files through symlinked paths inside the current session directory or configured roots.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = audience == TrustAudience.Public
            ? $"Error: {label} trust context may only access files inside the current session directory."
            : $"Error: {label} trust context may only access files inside the current session directory or configured roots: {string.Join(", ", roots)}.";
        return false;
    }

    private static ToolFilesystemAccessProfile GetAccessProfile(ToolAudienceProfile profile, AccessKind accessKind) =>
        accessKind switch
        {
            AccessKind.Read => profile.ReadFiles,
            AccessKind.Write => profile.WriteFiles,
            AccessKind.Attach => profile.AttachFiles,
            _ => profile.ReadFiles
        };

    /// <summary>
    /// Resolves profile roots and merges global read roots for read access.
    /// Single source of truth for root resolution — used by both
    /// <see cref="GetRootsForContext"/> and <see cref="TryResolvePath"/>.
    /// Public audience is excluded from global read roots (skills, identity,
    /// workspaces) — it may only access its session directory.
    /// </summary>
    private IReadOnlyList<string> ResolveAndMergeRoots(
        ToolFilesystemAccessProfile access,
        ToolExecutionContext context,
        TrustAudience audience,
        AccessKind accessKind)
    {
        var roots = _profileResolver.ResolveRoots(access, context)
            .Select(NormalizeDirectoryPath)
            .ToList();

        if (accessKind == AccessKind.Read && audience != TrustAudience.Public)
        {
            foreach (var globalRoot in _cachedGlobalReadRoots.Value)
                roots.Add(globalRoot);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static TrustAudience ResolveAudience(ToolExecutionContext context)
        => SecurityPolicyDefaults.TryParseAudience(context.Audience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(context.SessionId);

    private static string GetAudienceLabel(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "Public",
        TrustAudience.Team => "Team",
        TrustAudience.Personal => "Personal",
        _ => "Public"
    };

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
