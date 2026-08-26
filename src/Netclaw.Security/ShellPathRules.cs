// -----------------------------------------------------------------------
// <copyright file="ShellPathRules.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;

namespace Netclaw.Security;

internal static class ShellPathRules
{
    internal static bool TryNormalize(
        string? path,
        ShellPathStyle pathStyle,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || path.Any(char.IsControl)
            || pathStyle is not (ShellPathStyle.Posix or ShellPathStyle.Windows))
            return false;

        if (pathStyle == ShellPathStyle.Posix)
        {
            if (path[0] != '/')
                return false;

            normalized = NormalizeSegments(path, '/', "/");
            return normalized.Length > 0;
        }

        var windowsPath = path.Replace('/', '\\');
        var rootLength = GetWindowsRootLength(windowsPath);
        if (rootLength == 0)
            return false;

        var root = windowsPath[..rootLength];
        normalized = NormalizeSegments(windowsPath[rootLength..], '\\', root);
        return normalized.Length > 0;
    }

    internal static bool TryResolve(
        string? path,
        string? resolutionBase,
        ShellPathStyle pathStyle,
        out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrEmpty(path)
            || resolutionBase?.Any(char.IsControl) == true)
        {
            return false;
        }

        if (TryNormalize(path, pathStyle, out resolved))
            return true;

        if (pathStyle == ShellPathStyle.Windows
                && (path.StartsWith('\\')
                    || path.StartsWith('/')
                    || path.Contains(':', StringComparison.Ordinal))
            || path.StartsWith('~')
            || path.StartsWith("$HOME", StringComparison.Ordinal)
            || path.StartsWith("${HOME}", StringComparison.Ordinal)
            || path.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            || !TryNormalize(resolutionBase, pathStyle, out var normalizedBase))
        {
            return false;
        }

        var separator = pathStyle == ShellPathStyle.Windows ? '\\' : '/';
        var combined = normalizedBase.EndsWith(separator)
            ? normalizedBase + path
            : normalizedBase + separator + path;
        return TryNormalize(combined, pathStyle, out resolved);
    }

    internal static bool Equals(string left, string right, ShellPathStyle pathStyle)
        => string.Equals(
            left,
            right,
            pathStyle == ShellPathStyle.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    internal static bool IsWithinRoot(
        string candidate,
        string root,
        ShellPathStyle pathStyle)
    {
        if (Equals(candidate, root, pathStyle))
            return true;

        var separator = pathStyle == ShellPathStyle.Windows ? '\\' : '/';
        var prefix = root.EndsWith(separator) ? root : root + separator;
        return candidate.StartsWith(
            prefix,
            pathStyle == ShellPathStyle.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    internal static bool UsesHostPathStyle(ShellPathStyle pathStyle)
        => pathStyle switch
        {
            ShellPathStyle.Posix => !OperatingSystem.IsWindows(),
            ShellPathStyle.Windows => OperatingSystem.IsWindows(),
            _ => false
        };

    internal static bool TryGetRootRelativeDepth(
        string? path,
        ShellPathStyle pathStyle,
        out int depth)
    {
        depth = 0;
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return false;

        return pathStyle switch
        {
            ShellPathStyle.Posix => TryGetPosixDepth(path, out depth),
            ShellPathStyle.Windows => TryGetWindowsDepth(path, out depth),
            _ => false
        };
    }

    private static bool TryGetPosixDepth(string path, out int depth)
    {
        if (path == "/")
        {
            depth = 0;
            return true;
        }

        if (path[0] != '/'
            || path.EndsWith('/')
            || path.Contains("//", StringComparison.Ordinal))
        {
            depth = 0;
            return false;
        }

        return TryCountCanonicalSegments(path[1..], '/', out depth);
    }

    private static bool TryGetWindowsDepth(string path, out int depth)
    {
        depth = 0;
        if (path.Contains('/', StringComparison.Ordinal))
            return false;

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '\\')
        {
            if (path.Length == 3)
                return true;

            return TryCountCanonicalSegments(path[3..], '\\', out depth);
        }

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
            return false;

        var components = path[2..].Split('\\', StringSplitOptions.None);
        if (components.Length < 2
            || components[0] is "." or "?"
            || components.Any(static component =>
                component.Length == 0 || component is "." or ".."))
        {
            return false;
        }

        depth = components.Length - 2;
        return true;
    }

    private static bool TryCountCanonicalSegments(string path, char separator, out int depth)
    {
        var segments = path.Split(separator, StringSplitOptions.None);
        if (segments.Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            depth = 0;
            return false;
        }

        depth = segments.Length;
        return true;
    }

    private static string NormalizeSegments(string path, char separator, string root)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count == 0)
                    return string.Empty;

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
            return root;

        return root.EndsWith(separator)
            ? root + string.Join(separator, segments)
            : root + separator + string.Join(separator, segments);
    }

    private static int GetWindowsRootLength(string path)
    {
        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '\\')
        {
            return 3;
        }

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
            return 0;

        var serverEnd = path.IndexOf('\\', 2);
        if (serverEnd <= 2)
            return 0;

        var shareEnd = path.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0 ? path.Length : shareEnd + 1;
    }
}
