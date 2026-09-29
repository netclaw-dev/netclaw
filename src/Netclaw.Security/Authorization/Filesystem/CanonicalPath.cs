// -----------------------------------------------------------------------
// <copyright file="CanonicalPath.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;

namespace Netclaw.Security.Authorization.Filesystem;

/// <summary>
/// An absolute path in one declared path style, with no <c>.</c> or <c>..</c>
/// segment. The value is not link-resolved.
/// </summary>
/// <remarks>
/// Two factories exist because two path grammars exist. <see cref="TryCreate"/>
/// applies lexical rules for a declared shell style, so a parser path keeps its
/// meaning on a host with another style. <see cref="TryCreateHost"/> applies the
/// host path API, which file tools and stored grants use. Neither factory expands
/// <c>~</c>. A caller that needs home expansion expands the raw text first.
/// </remarks>
internal readonly record struct CanonicalPath
{
    private CanonicalPath(string value, ShellPathStyle style)
    {
        Value = value;
        Style = style;
    }

    internal string Value { get; }

    internal ShellPathStyle Style { get; }

    /// <summary>The host can inspect links and metadata only for its own path style.</summary>
    internal bool IsHostStyle => IsHostPathStyle(Style);

    internal static ShellPathStyle HostStyle { get; } =
        OperatingSystem.IsWindows() ? ShellPathStyle.Windows : ShellPathStyle.Posix;

    public override string ToString() => Value;

    internal static bool IsHostPathStyle(ShellPathStyle style) => style == HostStyle;

    /// <summary>
    /// Creates a lexical path for a declared shell style. A relative value needs a
    /// base. The factory refuses home tokens and a <c>..</c> above the root.
    /// </summary>
    internal static bool TryCreate(
        [NotNullWhen(true)] string? raw,
        string? relativeBase,
        ShellPathStyle style,
        out CanonicalPath path)
    {
        path = default;
        if (string.IsNullOrEmpty(raw) || relativeBase?.Any(char.IsControl) == true)
            return false;

        if (TryNormalize(raw, style, out var normalized))
        {
            path = new CanonicalPath(normalized, style);
            return true;
        }

        if (style == ShellPathStyle.Windows
                && (raw.StartsWith('\\') || raw.StartsWith('/') || raw.Contains(':', StringComparison.Ordinal))
            || raw.StartsWith('~')
            || raw.StartsWith("$HOME", StringComparison.Ordinal)
            || raw.StartsWith("${HOME}", StringComparison.Ordinal)
            || raw.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            || !TryNormalize(relativeBase, style, out var normalizedBase))
        {
            return false;
        }

        var separator = Separator(style);
        var combined = normalizedBase.EndsWith(separator)
            ? normalizedBase + raw
            : normalizedBase + separator + raw;
        if (!TryNormalize(combined, style, out normalized))
            return false;

        path = new CanonicalPath(normalized, style);
        return true;
    }

    /// <summary>
    /// Creates a host-style path with the host path API. A relative value resolves
    /// against <paramref name="relativeBase"/>, or the process directory when the
    /// base is blank. The value keeps <c>~</c> as a literal segment.
    /// </summary>
    internal static bool TryCreateHost([NotNullWhen(true)] string? raw, string? relativeBase, out CanonicalPath path)
    {
        path = default;
        if (string.IsNullOrEmpty(raw))
            return false;

        try
        {
            var combined = string.IsNullOrWhiteSpace(relativeBase) || Path.IsPathFullyQualified(raw)
                ? raw
                : Path.Combine(relativeBase, raw);
            path = new CanonicalPath(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined)),
                HostStyle);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the raw text has a <c>..</c> segment. Callers check the raw
    /// value before they create a path, because <c>..</c> after a link resolves
    /// differently on the host than it does lexically.
    /// </summary>
    internal static bool HasParentSegment(string path)
        => (OperatingSystem.IsWindows() ? path.Split(['/', '\\']) : path.Split('/'))
            .Any(static segment => segment == "..");

    /// <summary>
    /// Counts the segments below the volume root of a raw value that is already
    /// canonical. A device path, a relative path, or a non-canonical value fails.
    /// </summary>
    internal static bool TryGetRootDepth(string? path, ShellPathStyle style, out int depth)
    {
        depth = 0;
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return false;

        return style switch
        {
            ShellPathStyle.Posix => TryGetPosixDepth(path, out depth),
            ShellPathStyle.Windows => TryGetWindowsDepth(path, out depth),
            _ => false
        };
    }

    /// <summary>Returns true when <paramref name="path"/> is this directory or is below it.</summary>
    /// <remarks>
    /// Allow checks compare with case except for the Windows style (R4). Deny checks
    /// use <see cref="IsWithin"/> with <c>ignoreCase: true</c> instead.
    /// </remarks>
    internal bool Contains(CanonicalPath path)
        => Style == path.Style && IsWithin(path.Value, Value, Style, ignoreCase: false);

    internal bool IsSamePath(CanonicalPath other)
        => Style == other.Style && string.Equals(Value, other.Value, Comparison(Style, ignoreCase: false));

    /// <summary>The volume root of this path: <c>/</c>, a drive root, or a UNC share.</summary>
    internal CanonicalPath VolumeRoot
    {
        get
        {
            if (Style == ShellPathStyle.Posix)
                return new CanonicalPath("/", Style);

            var length = GetWindowsRootLength(Value);
            return new CanonicalPath(Value[..length], Style);
        }
    }

    /// <summary>The single containment rule. Every allow and deny comparison uses it.</summary>
    internal static bool IsWithin(string candidate, string root, ShellPathStyle style, bool ignoreCase)
    {
        var comparison = Comparison(style, ignoreCase);
        if (string.Equals(candidate, root, comparison))
            return true;

        var separator = Separator(style);
        var prefix = root.EndsWith(separator) ? root : root + separator;
        return candidate.StartsWith(prefix, comparison);
    }

    private static StringComparison Comparison(ShellPathStyle style, bool ignoreCase)
        => ignoreCase || style == ShellPathStyle.Windows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static char Separator(ShellPathStyle style) => style == ShellPathStyle.Windows ? '\\' : '/';

    private static bool TryNormalize(string? path, ShellPathStyle style, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || path.Any(char.IsControl)
            || style is not (ShellPathStyle.Posix or ShellPathStyle.Windows))
            return false;

        if (style == ShellPathStyle.Posix)
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

        normalized = NormalizeSegments(windowsPath[rootLength..], '\\', windowsPath[..rootLength]);
        return normalized.Length > 0;
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
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return 3;

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
            return 0;

        var serverEnd = path.IndexOf('\\', 2);
        if (serverEnd <= 2)
            return 0;

        var shareEnd = path.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0 ? path.Length : shareEnd + 1;
    }

    private static bool TryGetPosixDepth(string path, out int depth)
    {
        depth = 0;
        if (path == "/")
            return true;

        if (path[0] != '/' || path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal))
            return false;

        return TryCountCanonicalSegments(path[1..], '/', out depth);
    }

    // Stricter than TryCreate on purpose: an empty, "." or ".." UNC component
    // fails, so a malformed share never counts as a deep folder.
    private static bool TryGetWindowsDepth(string path, out int depth)
    {
        depth = 0;
        if (path.Contains('/', StringComparison.Ordinal))
            return false;

        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return path.Length == 3 || TryCountCanonicalSegments(path[3..], '\\', out depth);

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
            return false;

        var components = path[2..].Split('\\', StringSplitOptions.None);
        if (components.Length < 2
            || components[0] is "." or "?"
            || components.Any(static component => component.Length == 0 || component is "." or ".."))
        {
            return false;
        }

        depth = components.Length - 2;
        return true;
    }

    private static bool TryCountCanonicalSegments(string path, char separator, out int depth)
    {
        var segments = path.Split(separator, StringSplitOptions.None);
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            depth = 0;
            return false;
        }

        depth = segments.Length;
        return true;
    }
}
