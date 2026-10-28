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
/// Netclaw does not expand the glob. The walk retains target scopes and the
/// remaining parser segments for protection and grant checks.
/// </remarks>
internal static class ShellGlobScope
{
    // Each intermediate directory visit and each link hop consumes one unit.
    private const int MaximumWalkWork = 4096;

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

    /// <summary>Retains known target reach even when another branch cannot be proved.</summary>
    internal static ShellGlobReach ResolveReach(CanonicalPath coveringDirectory, ShellValueDomain.PathPattern pattern)
    {
        var glob = pattern.Glob!;
        var hasTrailingSlash = pattern.Pattern.EndsWith('/');
        var scopes = new HashSet<CanonicalPath> { coveringDirectory };
        var targets = new HashSet<ShellGlobTargetReach>();
        var complete = coveringDirectory.IsHostStyle;
        var walked = 0;
        var visited = new HashSet<(CanonicalPath Directory, int Segment)>();
        var current = new List<WalkDirectory> { new(coveringDirectory, [coveringDirectory]) };
        var held = new PathBoundary[] { new PathBoundary.Folder(coveringDirectory, LinkRule.BelowRoot) };

        if (!coveringDirectory.IsHostStyle)
            return Result();

        try
        {
            if (!Directory.Exists(coveringDirectory.Value))
            {
                complete &= FileSystemAuthority.FollowLinkChain(coveringDirectory.Value, out _) is LinkChainEnd.NotALink;
                return Result();
            }

            for (var index = 0; index < glob.SegmentDepth; index++)
            {
                var segment = glob.Segments[index];
                var requiresDirectory = index < glob.SegmentDepth - 1 || hasTrailingSlash;
                var next = new List<WalkDirectory>();
                foreach (var directory in current)
                {
                    if (!visited.Add((directory.Path, index)))
                        continue;

                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path.Value))
                        {
                            var attributes = File.GetAttributes(entry);
                            var isDirectory = (attributes & FileAttributes.Directory) != 0;
                            var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                            var matches = SegmentMayMatch(segment, Path.GetFileName(entry));
                            var path = entry;
                            if (isLink)
                            {
                                var end = FileSystemAuthority.FollowLinkChain(entry, out path, out var knownTargets);
                                var targetExists = end is LinkChainEnd.Target && Path.Exists(path);
                                isDirectory = targetExists && Directory.Exists(path);
                                // An unknown target type keeps its known reach. A proved file cannot fill a directory segment.
                                if (requiresDirectory && targetExists && !isDirectory)
                                    matches = false;
                                // Only matched intermediate directories receive the wider original-cover boundary.
                                var linkBoundary = matches && isDirectory && index < glob.SegmentDepth - 1
                                    ? coveringDirectory
                                    : directory.Path;
                                walked += Math.Max(1, knownTargets.Count);
                                foreach (var known in knownTargets)
                                {
                                    var knownPath = default(CanonicalPath);
                                    if (!CanonicalPath.TryCreateHost(known, null, out knownPath))
                                    {
                                        complete = false;
                                        continue;
                                    }

                                    if (matches)
                                        targets.Add(new ShellGlobTargetReach(knownPath, index + 1));
                                    if (!coveringDirectory.Contains(knownPath))
                                        complete = false;
                                }

                                if (walked > MaximumWalkWork)
                                {
                                    complete = false;
                                    return Result();
                                }

                                var target = default(CanonicalPath);
                                if (!targetExists
                                    || !CanonicalPath.TryCreateHost(path, null, out target)
                                    || FileSystemAuthority.EvaluateMembership(target, held) is not PathDecision.Allowed
                                    || FileSystemAuthority.EvaluateMembership(target,
                                        [new PathBoundary.Folder(linkBoundary, LinkRule.BelowRoot)]) is not PathDecision.Allowed)
                                {
                                    complete = false;
                                    continue;
                                }

                                if (!matches)
                                    continue;

                                scopes.Add(directory.Path);
                                var parent = default(CanonicalPath);
                                scopes.Add(isDirectory
                                    ? target
                                    : CanonicalPath.TryCreateHost(Path.GetDirectoryName(path), null, out parent) ? parent : target);
                                if (directory.Ancestors.Contains(target) || target.Contains(directory.Path))
                                {
                                    complete = false;
                                    continue;
                                }
                            }

                            if (!matches || !isDirectory || index == glob.SegmentDepth - 1)
                                continue;
                            if (++walked > MaximumWalkWork)
                            {
                                complete = false;
                                return Result();
                            }

                            var child = default(CanonicalPath);
                            if (!CanonicalPath.TryCreateHost(path, null, out child))
                            {
                                complete = false;
                                continue;
                            }
                            next.Add(new WalkDirectory(child, [.. directory.Ancestors, child]));
                        }
                    }
                    catch (Exception ex) when (FileSystemAuthority.IsInspectionFailure(ex))
                    {
                        complete = false;
                    }
                }
                current = next;
            }
        }
        catch (Exception ex) when (FileSystemAuthority.IsInspectionFailure(ex))
        {
            complete = false;
        }

        return Result();

        ShellGlobReach Result()
        {
            var scopePaths = scopes.ToArray();
            var targetReach = targets.ToArray();
            return complete
                ? new ShellGlobReach.Complete(scopePaths, targetReach)
                : new ShellGlobReach.Unresolved(scopePaths, targetReach);
        }
    }

    private sealed record WalkDirectory(CanonicalPath Path, IReadOnlyList<CanonicalPath> Ancestors);

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
        => MatchPathToward(pattern.CoveringDirectory, pattern.Glob!.Segments, 0, target);

    /// <summary>Gets the lexical reach below a known alias target for the remaining parser segments.</summary>
    internal static string? MatchPathToward(
        ShellValueDomain.PathPattern pattern,
        ShellGlobTargetReach reach,
        string target)
        => MatchPathToward(reach.Target.Value, pattern.Glob!.Segments, reach.NextSegmentIndex, target);

    private static string? MatchPathToward(
        string coveringDirectory,
        IReadOnlyList<ShellGlobSegment> segments,
        int nextSegmentIndex,
        string target)
    {
        const char separator = '/';
        var prefix = coveringDirectory.EndsWith(separator)
            ? coveringDirectory
            : coveringDirectory + separator;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = target[prefix.Length..].Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (relative.Length == 0)
            return null;

        var reach = Math.Min(relative.Length, segments.Count - nextSegmentIndex);
        for (var index = 0; index < reach; index++)
        {
            if (!SegmentMayMatch(segments[nextSegmentIndex + index], relative[index]))
                return null;
        }

        return reach == 0 ? null : prefix + string.Join(separator, relative[..reach]);
    }

    private static string ToSimpleExpression(string text)
        => text.Contains('[', StringComparison.Ordinal) ? "*" : text;
}

internal readonly record struct ShellGlobTargetReach(CanonicalPath Target, int NextSegmentIndex);

/// <summary>Known paths remain visible to protection when reusable scope is unresolved.</summary>
internal abstract record ShellGlobReach(IReadOnlyList<CanonicalPath> Scopes, IReadOnlyList<ShellGlobTargetReach> Targets)
{
    internal sealed record Complete(IReadOnlyList<CanonicalPath> Paths, IReadOnlyList<ShellGlobTargetReach> KnownTargets)
        : ShellGlobReach(Paths, KnownTargets);

    internal sealed record Unresolved(IReadOnlyList<CanonicalPath> Paths, IReadOnlyList<ShellGlobTargetReach> KnownTargets)
        : ShellGlobReach(Paths, KnownTargets);
}
