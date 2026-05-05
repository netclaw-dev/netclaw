// -----------------------------------------------------------------------
// <copyright file="PathUtility.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

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
    {
        var normalizedCandidate = Normalize(candidate);
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
    /// Expands shell path tokens: ~, $HOME, ${HOME}, %USERPROFILE%.
    /// Does not normalize the path.
    /// </summary>
    public static string ExpandHome(string path)
    {
        var expanded = path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (expanded.StartsWith('~'))
        {
            if (!string.IsNullOrWhiteSpace(home))
            {
                expanded = expanded.Length == 1
                    ? home
                    : Path.Combine(home, expanded[1..].TrimStart('/', '\\'));
            }
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            expanded = expanded.Replace("$HOME", home, StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace("${HOME}", home, StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace("%USERPROFILE%", home, StringComparison.OrdinalIgnoreCase);
        }

        return expanded;
    }

    /// <summary>
    /// Expands shell path tokens and normalizes relative to working directory.
    /// Returns null if expansion or normalization fails.
    /// </summary>
    public static string? ExpandAndNormalize(string path, string? workingDirectory = null)
    {
        var expanded = ExpandHome(path);
        return TryNormalize(expanded, workingDirectory, out var normalized) ? normalized : null;
    }
}
