// -----------------------------------------------------------------------
// <copyright file="PathUtility.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Centralized path utilities for security-sensitive path operations.
/// </summary>
public static class PathUtility
{
    /// <summary>
    /// Normalizes a path by resolving to full path and removing trailing separators.
    /// </summary>
    public static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Attempts to normalize a path, returning false if the path is invalid.
    /// </summary>
    public static bool TryNormalize(string path, out string normalized)
        => TryNormalize(path, workingDirectory: null, out normalized);

    /// <summary>
    /// Attempts to normalize a path relative to a working directory.
    /// </summary>
    public static bool TryNormalize(string path, string? workingDirectory, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var baseDir = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Environment.CurrentDirectory;

            normalized = Path.IsPathRooted(path)
                ? Normalize(path)
                : Normalize(Path.Combine(baseDir, path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a candidate path is within or equal to a root directory.
    /// Uses platform-appropriate case sensitivity.
    /// </summary>
    public static bool IsWithinRoot(string candidate, string root)
        => IsNormalizedWithinRoot(Normalize(candidate), root);

    /// <summary>
    /// Like <see cref="IsWithinRoot"/> but assumes <paramref name="normalizedCandidate"/>
    /// has already been canonicalized via <see cref="Normalize"/>. Use on hot
    /// paths that compare many roots against the same candidate to avoid
    /// re-running <c>Path.GetFullPath</c> on the candidate per iteration.
    /// </summary>
    public static bool IsNormalizedWithinRoot(string normalizedCandidate, string root)
    {
        var normalizedRoot = Normalize(root);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedCandidate.StartsWith(normalizedRoot, comparison))
            return false;

        if (normalizedCandidate.Length == normalizedRoot.Length)
            return true;

        var boundary = normalizedCandidate[normalizedRoot.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Returns true when <paramref name="a"/> and <paramref name="b"/> resolve
    /// to the same canonical filesystem path under the platform's casing
    /// rules. Both inputs are passed through <see cref="Normalize"/> first.
    /// Swallows <see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
    /// and <see cref="System.Security.SecurityException"/> (returning false) so
    /// callers can compare untrusted or partially-formed paths without
    /// wrapping the call in a try/catch.
    /// </summary>
    public static bool AreEquivalentPaths(string a, string b)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Normalize(a), Normalize(b), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a candidate path is within or equal to any of the root directories.
    /// Uses platform-appropriate case sensitivity.
    /// </summary>
    public static bool IsWithinAnyRoot(string candidate, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (IsWithinRoot(candidate, root))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Appends a trailing directory separator to <paramref name="path"/> if it does
    /// not already end with one, preserving the path's existing separator style.
    /// </summary>
    /// <remarks>
    /// A path that already contains backslashes (typical of Windows host-resolved
    /// paths from <see cref="Path.GetFullPath(string)"/>) gets a backslash appended,
    /// while a path with only forward slashes — or no separators at all — gets a
    /// forward slash. This avoids producing mixed-separator strings such as
    /// <c>C:\foo\bar/</c> when POSIX-flavored shell semantics are applied to a
    /// host-resolved Windows path.
    /// </remarks>
    public static string EnsureTrailingSeparatorPreservingStyle(string path)
    {
        if (path.Length == 0)
            return path;

        var last = path[^1];
        if (last == '/' || last == '\\')
            return path;

        return path.Contains('\\', StringComparison.Ordinal)
            ? path + '\\'
            : path + '/';
    }

    /// <summary>
    /// Expands shell path tokens: ~, $HOME, ${HOME}, %USERPROFILE%. Does not
    /// normalize the path. Delegates to <see cref="PathExpansion.ExpandHome"/>
    /// so Configuration and Security share one canonical implementation;
    /// preserves the historical non-null contract by passing through the
    /// original input on whitespace-only values.
    /// </summary>
    public static string ExpandHome(string path)
        => PathExpansion.ExpandHome(path) ?? path;

    /// <summary>
    /// Expands shell path tokens and normalizes relative to working directory.
    /// Returns null if expansion or normalization fails.
    /// </summary>
    public static string? ExpandAndNormalize(string path, string? workingDirectory = null)
    {
        var expanded = ExpandHome(path);
        return TryNormalize(expanded, workingDirectory, out var normalized) ? normalized : null;
    }

    /// <summary>
    /// Returns true when any segment of <paramref name="fullPath"/>, walking from
    /// <paramref name="allowedRoot"/> outward, is a filesystem reparse point
    /// (symbolic link, junction, or other reparse target). Used by the approval
    /// gate and file-access policy to refuse to honor a grant when the candidate
    /// path's resolution depends on a symlink that could redirect the I/O outside
    /// the granted root.
    ///
    /// Errors reading attributes are conservatively treated as a positive
    /// detection: if we cannot determine whether a segment is a symlink, we
    /// assume it is.
    /// </summary>
    public static bool ContainsSymlinkSegment(string allowedRoot, string fullPath)
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
}
