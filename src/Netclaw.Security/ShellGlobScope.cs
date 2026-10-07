// -----------------------------------------------------------------------
// <copyright file="ShellGlobScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Enumeration;
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// The reach of a Bash glob word from the ShellSyntaxTree glob facts: the
/// covering directory and the segments below it, to the segment depth.
/// </summary>
/// <remarks>
/// ShellSyntaxTree 0.4.0-beta.11 publishes the facts only when the shell options
/// are proved (globstar, dotglob, nullglob, failglob, and extglob off;
/// globskipdots on). Each match of the word is a path below the covering
/// directory with exactly <see cref="ShellGlobExpansion.SegmentDepth"/> segments.
/// Netclaw does not expand the glob. The protected-path check is lexical, and the
/// link check walks only the directories that a segment can match.
/// </remarks>
internal static class ShellGlobScope
{
    // The plan bound for the link walk. A larger tree keeps the word unresolved.
    private const int MaximumWalkedDirectories = 4096;

    /// <summary>Gets the glob fact of a parser argument, or null when the parser gives none.</summary>
    internal static ShellValueDomain.PathPattern? FindGlobPattern(CommandOccurrence occurrence, Arg arg)
    {
        foreach (var argument in occurrence.Arguments)
        {
            if (ReferenceEquals(argument.Argument, arg))
                return AsGlobPattern(argument.Value);
        }

        return null;
    }

    /// <summary>Returns the pattern when it carries a glob fact with a covering directory.</summary>
    internal static ShellValueDomain.PathPattern? AsGlobPattern(ShellValueDomain domain)
        => domain is ShellValueDomain.PathPattern { Glob: { SegmentDepth: > 0 } glob } pattern
           && glob.Segments.Count == glob.SegmentDepth
           && !string.IsNullOrWhiteSpace(pattern.CoveringDirectory)
            ? pattern
            : null;

    /// <summary>
    /// Returns true when the segment can match the entry name. The check is lexical
    /// and errs toward a match.
    /// </summary>
    /// <remarks>
    /// SECURITY: a segment with a bracket expression can match any name. The
    /// comparison ignores case, as the protected sets do (R4). A
    /// leading dot matches only when the parser says the segment can match a dot
    /// entry, because dotglob is off.
    /// </remarks>
    internal static bool SegmentMayMatch(ShellGlobSegment segment, string name)
    {
        if (!segment.IsPattern)
            return string.Equals(segment.Text, name, StringComparison.OrdinalIgnoreCase);

        if (name.StartsWith('.') && !segment.MayMatchDotEntry)
            return false;

        return FileSystemName.MatchesSimpleExpression(
            ToSimpleExpression(segment.Text),
            name,
            ignoreCase: true);
    }

    /// <summary>
    /// Returns true when a link cannot take a match of the word out of the covering
    /// directory. Each walked directory must keep its link entries inside itself.
    /// </summary>
    /// <remarks>
    /// The walk visits only the directories that each segment can match, to one
    /// level above the segment depth. A link on the way to the last segment keeps
    /// the word unresolved, and so does a tree with more than 4096 directories. A
    /// covering directory that does not exist has no match, so Bash keeps the word
    /// as literal text inside that directory.
    /// </remarks>
    internal static bool IsLinkContained(CanonicalPath coveringDirectory, ShellGlobExpansion glob)
    {
        if (!Directory.Exists(coveringDirectory.Value))
            return true;

        try
        {
            var current = new List<string> { coveringDirectory.Value };
            var walked = 0;
            foreach (var segment in glob.Segments.SkipLast(1))
            {
                var next = new List<string>();
                foreach (var directory in current)
                {
                    if (!HasOnlyContainedLinkEntries(directory))
                        return false;

                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (!SegmentMayMatch(segment, Path.GetFileName(child)))
                            continue;

                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0
                            || ++walked > MaximumWalkedDirectories)
                        {
                            return false;
                        }

                        next.Add(child);
                    }
                }

                current = next;
            }

            return current.All(HasOnlyContainedLinkEntries);
        }
        catch (Exception ex) when (FileSystemAuthority.IsInspectionFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Returns each path that the word can reach as a match or as a directory on the
    /// way to a match, when that path is <paramref name="target"/> or contains it.
    /// </summary>
    /// <remarks>
    /// The result is the lexical path of the match level: the target itself when the
    /// target is at most <see cref="ShellGlobExpansion.SegmentDepth"/> segments below
    /// the covering directory, or the ancestor of the target at that depth. The
    /// caller judges that path as a literal operand.
    /// </remarks>
    internal static string? MatchPathToward(ShellValueDomain.PathPattern pattern, string target)
    {
        // The parser gives glob facts only for the no-startup Bash host, so the
        // paths use POSIX separators.
        const char separator = '/';
        var glob = pattern.Glob!;
        var prefix = pattern.CoveringDirectory.EndsWith(separator)
            ? pattern.CoveringDirectory
            : pattern.CoveringDirectory + separator;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = target[prefix.Length..].Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (relative.Length == 0)
            return null;

        var reach = Math.Min(relative.Length, glob.SegmentDepth);
        for (var index = 0; index < reach; index++)
        {
            if (!SegmentMayMatch(glob.Segments[index], relative[index]))
                return null;
        }

        return prefix + string.Join(separator, relative[..reach]);
    }

    // A bracket expression has its own syntax ("[!a-z]", "[]x]"). The check does
    // not read it: a segment with "[" can match any name. The result is a superset.
    private static bool HasOnlyContainedLinkEntries(string directory)
        => CanonicalPath.TryCreateHost(directory, null, out var canonical)
           && FileSystemAuthority.HasOnlyContainedLinkEntries(canonical);

    private static string ToSimpleExpression(string text)
        => text.Contains('[', StringComparison.Ordinal) ? "*" : text;
}
