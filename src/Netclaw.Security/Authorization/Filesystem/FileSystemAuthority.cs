// -----------------------------------------------------------------------
// <copyright file="FileSystemAuthority.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Security.Authorization.Filesystem;

/// <summary>Where a link check starts, and whether its start directory counts.</summary>
/// <remarks>
/// The rules stay separate on purpose (R3). One shared rule would either break the
/// macOS temporary alias or let a link above a grant root redirect a repository.
/// </remarks>
internal enum LinkRule
{
    /// <summary>
    /// Refuses a link below the link anchor. The anchor and its ancestors are
    /// trusted. Folder grants, causal intent, skill resources, and skill_manage use it.
    /// </summary>
    BelowRoot,

    /// <summary>
    /// Refuses a link at the link anchor or below it. File-tool roots, generated
    /// destinations, the tool output spill, and managed temporary storage use it.
    /// </summary>
    IncludingRoot,

    /// <summary>Refuses a link anywhere from the volume root. Repository identity uses it.</summary>
    FromVolumeRoot,

    /// <summary>
    /// <see cref="FromVolumeRoot"/>, except that a path below a platform temporary
    /// root passes when no link exists below the resolved temporary root. This is
    /// the macOS <c>/tmp</c> to <c>/private/tmp</c> alias (R7). It is a link rule,
    /// not a boundary: it never makes a temporary directory a grant root.
    /// </summary>
    FromVolumeRootExceptTemporaryAlias,
}

/// <summary>The protected-path set that applies to an operation (R2).</summary>
internal enum PathOperation
{
    /// <summary>Structured reads, lists, searches, attachments, and project declarations.</summary>
    Read,

    /// <summary>Structured writes and generated files.</summary>
    Write,

    /// <summary>Paths that shell command text names. The shell list includes the configuration directory.</summary>
    Shell,
}

/// <summary>A boundary that a caller holds. The caller chooses the boundaries; the authority never looks them up.</summary>
/// <remarks>
/// File tools get boundaries from the audience profile, session, and project.
/// Consent gets them from grants. This separation keeps the rule that a shell grant
/// never authorizes <c>file_read</c> (R8).
/// </remarks>
internal abstract record PathBoundary
{
    private PathBoundary() { }

    /// <summary>A directory and everything below it.</summary>
    internal sealed record Folder(CanonicalPath Root, LinkRule Links) : PathBoundary
    {
        private readonly CanonicalPath _linkAnchor = Root;

        /// <summary>
        /// Where the link check starts. It is the root unless the caller names an
        /// enclosing directory, for example the session storage root.
        /// </summary>
        internal CanonicalPath LinkAnchor
        {
            get => _linkAnchor;
            init => _linkAnchor = value.Contains(Root)
                ? value
                : throw new ArgumentException("A link anchor must contain the folder root.", nameof(value));
        }
    }

    /// <summary>
    /// One file and nothing beside it (R11). The legacy session log uses it, so the
    /// other logs in the same directory stay out of reach.
    /// </summary>
    internal sealed record ExactFile(CanonicalPath File) : PathBoundary
    {
        private readonly CanonicalPath _linkAnchor = File;

        /// <summary>Where the link check starts. The check includes the anchor.</summary>
        internal CanonicalPath LinkAnchor
        {
            get => _linkAnchor;
            init => _linkAnchor = value.Contains(File)
                ? value
                : throw new ArgumentException("A link anchor must contain the file.", nameof(value));
        }
    }

    /// <summary>
    /// Any path. Only the file-tool root source produces it, for an interactive
    /// Mode.All profile (R1). Protection still applies. A grant never carries it.
    /// </summary>
    internal sealed record Unrestricted : PathBoundary;
}

/// <summary>Where a link chain ends (<see cref="FileSystemAuthority.FollowLinkChain"/>).</summary>
internal enum LinkChainEnd
{
    /// <summary>The path is not a link. A missing path is not a link.</summary>
    NotALink,

    /// <summary>The chain ends at a known target. The target can be missing.</summary>
    Target,

    /// <summary>The target is not known. Callers fail closed.</summary>
    Unknown,
}

/// <summary>The answer for one path. The first held boundary that contains the path decides.</summary>
/// <remarks>No case carries data: an allowed path is the path the caller passed.</remarks>
internal enum PathDecision
{
    Allowed,

    /// <summary>The path is protected for the operation. Protection wins over every boundary.</summary>
    Protected,

    Outside,

    CrossesLink,

    /// <summary>The host could not inspect the path. Callers fail closed.</summary>
    Unverifiable,
}

/// <summary>
/// Answers three questions about a path: its exact form (<see cref="CanonicalPath"/>),
/// its membership in a held boundary, and its protection for an operation. It also
/// resolves repository identity (<see cref="RepositoryIdentity"/>).
/// </summary>
/// <remarks>
/// <para>
/// The authority does not know audiences, grants, or shell syntax. Instance data is
/// the protected-path sets, built once per process. Everything else reads the
/// filesystem on each call and caches nothing.
/// </para>
/// <para>
/// Failure policy (R13): construction skips a protected path that fails link
/// resolution, because the lexical entry still denies. Evaluation fails closed.
/// </para>
/// </remarks>
internal sealed class FileSystemAuthority
{
    private static readonly Lazy<IReadOnlyList<PlatformTemporaryRoot>> HostTemporaryRoots =
        new(() => PlatformTemporaryRoot.ResolveAll(
            OperatingSystem.IsWindows() ? [Path.GetTempPath()] : [Path.GetTempPath(), "/tmp"]));

    private readonly HashSet<string> _writeProtected;
    private readonly HashSet<string> _readProtected;
    private readonly HashSet<string> _shellProtected;

    internal FileSystemAuthority(
        IEnumerable<string> writeProtected,
        IEnumerable<string> readProtected,
        IEnumerable<string> shellProtected)
    {
        _writeProtected = BuildProtectedSet(writeProtected);
        _readProtected = BuildProtectedSet(readProtected);
        _shellProtected = BuildProtectedSet(shellProtected);
    }

    /// <summary>Checks membership in the held boundaries, and then protection for the operation.</summary>
    /// <remarks>
    /// Membership runs first so that each caller keeps its current error text. The
    /// order does not change an outcome: an allowed membership never skips protection.
    /// </remarks>
    internal PathDecision Evaluate(CanonicalPath path, PathOperation operation, IReadOnlyList<PathBoundary> held)
    {
        var membership = EvaluateMembership(path, held);
        if (membership is not PathDecision.Allowed)
            return membership;

        return IsProtected(path.Value, operation) ? PathDecision.Protected : membership;
    }

    /// <summary>Checks membership only. Callers that screen protection at another stage use it.</summary>
    internal static PathDecision EvaluateMembership(CanonicalPath path, IReadOnlyList<PathBoundary> held)
    {
        foreach (var boundary in held)
        {
            var decision = boundary switch
            {
                PathBoundary.Unrestricted => PathDecision.Allowed,
                PathBoundary.Folder folder => EvaluateFolder(path, folder),
                PathBoundary.ExactFile file => EvaluateExactFile(path, file),
                _ => throw new InvalidOperationException($"Unknown path boundary {boundary.GetType().Name}.")
            };
            if (decision is not PathDecision.Outside)
                return decision;
        }

        return PathDecision.Outside;
    }

    /// <summary>Gets the normalized protected paths of an operation.</summary>
    /// <remarks>A glob word has no single path, so the shell screen compares its segments with each entry.</remarks>
    internal IReadOnlyCollection<string> GetProtectedPaths(PathOperation operation)
        => operation switch
        {
            PathOperation.Read => _readProtected,
            PathOperation.Write => _writeProtected,
            PathOperation.Shell => _shellProtected,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    /// <summary>Returns true when a path is protected for an operation.</summary>
    /// <remarks>
    /// Protected sets compare without case on every host (R4). The check compares
    /// the lexical path, the final link target, and the fully link-resolved path. A
    /// resolution failure counts as protected (R13).
    /// </remarks>
    internal bool IsProtected(string path, PathOperation operation)
    {
        var protectedSet = operation switch
        {
            PathOperation.Read => _readProtected,
            PathOperation.Write => _writeProtected,
            PathOperation.Shell => _shellProtected,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (PathUtility.TryNormalize(path, null, out var normalized) && IsInProtectedSet(normalized, protectedSet))
            return true;

        try
        {
            // A final-element link and an intermediate directory link can each
            // redirect the path into a protected location.
            if (TryResolveFinalLink(path, out var target) && IsInProtectedSet(target, protectedSet))
                return true;

            // SECURITY: the host resolver removes a ".." in a link text lexically,
            // but the OS follows a link before it applies "..". For such a link the
            // screen reads the path that the OS opens. A path that this walk
            // cannot resolve counts as protected (R13).
            var fullPath = Path.GetFullPath(path);
            if (FollowLinkChain(fullPath, out _) is LinkChainEnd.Unknown
                && (!TryResolvePhysicalPath(fullPath, out var physical)
                    || IsInProtectedSet(physical, protectedSet)))
            {
                return true;
            }

            return TryResolveLinks(path, out var resolved) && IsInProtectedSet(resolved, protectedSet);
        }
        catch
        {
            // This check is the only backstop for interactive Personal reads. An
            // undetermined resolution must deny (the #1724 defect class).
            return true;
        }
    }

    /// <summary>
    /// Returns true when a read-protected path is the path itself or is below
    /// it. A program that reads below a directory can reach that path.
    /// </summary>
    /// <remarks>
    /// The check compares the fully link-resolved path. The protected set holds
    /// the lexical and the link-resolved form of each entry. A resolution failure
    /// counts as holding a protected path.
    /// </remarks>
    internal bool HoldsReadProtectedPath(string path)
    {
        if (!PathUtility.TryNormalize(path, null, out var normalized))
            return true;

        try
        {
            TryResolveLinks(normalized, out var resolved);
            return _readProtected.Any(protectedPath =>
                CanonicalPath.IsWithin(protectedPath, resolved, CanonicalPath.HostStyle, ignoreCase: true));
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return true;
        }
    }

    /// <summary>
    /// Resolves every link in a path, segment by segment. Returns true only when the
    /// resolved path differs from the lexical path. <paramref name="resolved"/> is set in both cases.
    /// </summary>
    /// <remarks>
    /// Callers own the exception policy. Protected-set construction skips a failure;
    /// deny checks fail closed; the launch snapshot lets the failure stop the launch.
    /// </remarks>
    internal static bool TryResolveLinks(string path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrEmpty(path))
            return false;

        var fullPath = Path.GetFullPath(path);
        // Seed with the full root and split only the remainder. Splitting the whole
        // path repeats the drive segment ("C:\C:\Users") and resolution silently
        // does nothing (#1724).
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(root);

        foreach (var segment in segments)
        {
            if (builder.Length > 0 && builder[^1] != Path.DirectorySeparatorChar)
                builder.Append(Path.DirectorySeparatorChar);
            builder.Append(segment);

            var partial = builder.ToString();
            if (Directory.Exists(partial))
            {
                var target = new DirectoryInfo(partial).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    builder.Clear();
                    builder.Append(target.FullName);
                }
            }
            else if (File.Exists(partial))
            {
                var target = new FileInfo(partial).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    builder.Clear();
                    builder.Append(target.FullName);
                }

                break;
            }
        }

        resolved = PathUtility.Normalize(builder.ToString());
        return !string.IsNullOrEmpty(resolved)
               && !string.Equals(resolved, PathUtility.Normalize(fullPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true when no link exists from the volume root to <paramref name="path"/>
    /// under a volume rule. The host cannot inspect a path in another host's style,
    /// so that path passes here; the file-protection screen fails it closed.
    /// </summary>
    internal static bool IsLinkFreeFromVolumeRoot(CanonicalPath path, LinkRule volumeRule)
    {
        if (volumeRule is not (LinkRule.FromVolumeRoot or LinkRule.FromVolumeRootExceptTemporaryAlias))
            throw new ArgumentOutOfRangeException(nameof(volumeRule), volumeRule, "A volume link rule is required.");

        return EvaluateMembership(path, [new PathBoundary.Folder(path.VolumeRoot, volumeRule)])
            is PathDecision.Allowed;
    }

    /// <summary>
    /// Returns true when every link entry directly inside <paramref name="directory"/>
    /// has an existing final target inside it without another link. A glob that
    /// expands in the directory then stays in the directory.
    /// </summary>
    /// <remarks>
    /// Netclaw does not reproduce shell glob rules, so every link entry must pass.
    /// <see cref="FollowLinkChain"/> is the one reader of a link target, so a glob
    /// match and a literal word get the same answer for one link (#2375).
    /// </remarks>
    internal static bool HasOnlyContainedLinkEntries(CanonicalPath directory)
    {
        if (!directory.IsHostStyle || !Directory.Exists(directory.Value))
            return true;

        try
        {
            var boundary = new PathBoundary[] { new PathBoundary.Folder(directory, LinkRule.BelowRoot) };
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Value))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    continue;

                // A reparse point that is not a link has no target to judge. A
                // glob gives no match for a dangling link, so such an entry
                // keeps the word unresolved.
                if (FollowLinkChain(entry, out var target) is not LinkChainEnd.Target
                    || !Path.Exists(target)
                    || !CanonicalPath.TryCreateHost(target, null, out var targetPath)
                    || EvaluateMembership(targetPath, boundary) is not PathDecision.Allowed)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is below a host platform temporary
    /// root, by its authored or resolved name, and no link exists below the resolved root.
    /// </summary>
    internal static bool IsBelowTemporaryAlias(CanonicalPath path)
        => IsBelowTemporaryAlias(path, HostTemporaryRoots.Value);

    internal static bool IsBelowTemporaryAlias(CanonicalPath path, IReadOnlyList<PlatformTemporaryRoot> roots)
    {
        foreach (var root in roots)
        {
            if (root.TryMapToResolved(path, out var resolved)
                && resolved.IsHostStyle
                && !CrossesLink(root.Resolved.Value, resolved.Value, includeAnchor: false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a <c>..</c> segment in the raw host path leaves a link, or
    /// leaves a segment that the host cannot inspect. The OS follows a link before it
    /// applies <c>..</c>, so the lexical form of such a path does not name the file
    /// that the OS opens. A relative path resolves against <paramref name="baseDirectory"/>.
    /// </summary>
    /// <remarks>
    /// A path without a <c>..</c> segment returns false and causes no I/O. The check
    /// reads each character literally; the caller expands the text first. A missing
    /// segment is not a link, as in <see cref="CrossesLink"/>.
    /// </remarks>
    internal static bool HasParentSegmentAfterLink(string path, string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!CanonicalPath.HasParentSegment(path))
            return false;

        try
        {
            string fullPath;
            if (Path.IsPathFullyQualified(path))
                fullPath = path;
            else if (!Path.IsPathRooted(path)
                     && !string.IsNullOrWhiteSpace(baseDirectory)
                     && Path.IsPathFullyQualified(baseDirectory))
                fullPath = Path.Join(baseDirectory, path);
            else
                return true;

            char[] separators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];
            var parent = Path.GetPathRoot(fullPath)!;
            var parents = new Stack<string>();
            foreach (var segment in fullPath[parent.Length..].Split(separators))
            {
                if (segment is "" or ".")
                    continue;

                if (segment != "..")
                {
                    parents.Push(parent);
                    parent = Path.Combine(parent, segment);
                    continue;
                }

                if (parents.Count == 0)
                    continue;

                // The ".." leaves the last segment of `parent`. The single walker
                // checks exactly that segment.
                if (CrossesLink(parents.Peek(), parent, includeAnchor: false))
                    return true;

                parent = parents.Pop();
            }

            return false;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return true;
        }
    }

    /// <summary>
    /// Follows the link chain that starts at <paramref name="path"/> and gives its
    /// final target in <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target keeps the lexical frame of the path. A relative link text resolves
    /// against the lexical directory of its link, so a link above a grant root (an OS
    /// alias) stays in the target, as it is in the path. A directory link in the target
    /// stays too, so the caller's link walk still sees it.
    /// </para>
    /// <para>
    /// SECURITY: the lexical form names the file that the OS opens only when no
    /// <c>..</c> in a link text leaves a link. Such a chain gives
    /// <see cref="LinkChainEnd.Unknown"/>, and so do a loop, more than
    /// <see cref="MaximumLinkHops"/> links, a rooted text that is not a full path, and
    /// an inspection failure. Callers fail closed on <see cref="LinkChainEnd.Unknown"/>.
    /// </para>
    /// <para>
    /// A missing final target (a dangling link) is a known target. The link text
    /// states the path, and a write through the link creates the file there. So the
    /// caller judges that path: a target in the scope is covered, and a target
    /// outside the scope is not.
    /// </para>
    /// </remarks>
    internal static LinkChainEnd FollowLinkChain(string path, out string target)
    {
        ArgumentNullException.ThrowIfNull(path);
        target = string.Empty;
        // The host cannot name a path that is not a full path, so it is not a
        // link that this check can read. Unknown means a link without a known target.
        if (!Path.IsPathFullyQualified(path))
            return LinkChainEnd.NotALink;

        var current = path;
        try
        {
            for (var hops = 0; ; hops++)
            {
                var linkText = ReadLinkText(current);
                if (linkText is null)
                {
                    if (hops == 0)
                        return LinkChainEnd.NotALink;

                    target = current;
                    return LinkChainEnd.Target;
                }

                if (hops == MaximumLinkHops)
                    return LinkChainEnd.Unknown;

                string next;
                if (Path.IsPathFullyQualified(linkText))
                    next = linkText;
                else if (Path.IsPathRooted(linkText))
                    return LinkChainEnd.Unknown;
                else
                    next = Path.Join(Path.GetDirectoryName(current), linkText);

                if (HasParentSegmentAfterLink(next, baseDirectory: null))
                    return LinkChainEnd.Unknown;

                current = Path.GetFullPath(next);
            }
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return LinkChainEnd.Unknown;
        }
    }

    /// <summary>
    /// Resolves a full path as the OS does: one segment at a time, a link before
    /// the next segment, and <c>..</c> against the resolved parent. Returns false
    /// for a loop, for more than <see cref="MaximumLinkHops"/> links, and for a
    /// rooted link text that is not a full path.
    /// </summary>
    /// <remarks>
    /// Only the protected-path screen uses it, and only for a link that
    /// <see cref="FollowLinkChain"/> cannot name in the lexical frame. The result
    /// has no alias of a grant root, so it is not a grant scope.
    /// </remarks>
    private static bool TryResolvePhysicalPath(string path, out string physical)
    {
        physical = string.Empty;
        char[] separators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];
        var current = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(current))
            return false;

        var pending = new Stack<string>(path[current.Length..].Split(separators).Reverse());
        var hops = 0;
        while (pending.TryPop(out var segment))
        {
            if (segment is "" or ".")
                continue;

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var entry = Path.Combine(current, segment);
            var linkText = ReadLinkText(entry);
            if (linkText is null)
            {
                current = entry;
                continue;
            }

            if (++hops > MaximumLinkHops)
                return false;

            if (Path.IsPathFullyQualified(linkText))
            {
                current = Path.GetPathRoot(linkText)!;
                linkText = linkText[current.Length..];
            }
            else if (Path.IsPathRooted(linkText))
            {
                return false;
            }

            foreach (var linkSegment in linkText.Split(separators).Reverse())
                pending.Push(linkSegment);
        }

        physical = PathUtility.Normalize(current);
        return true;
    }

    /// <summary>The most links that <see cref="FollowLinkChain"/> follows. Linux uses 40.</summary>
    internal const int MaximumLinkHops = 40;

    // Returns the text of a link, or null for a missing entry or an entry that is not
    // a link. A reparse point that is not a link (no link text) ends the chain.
    private static string? ReadLinkText(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            // No entry, or an entry that this user cannot name: a missing file, a
            // name that is too long (a text word that only looks like a path), a
            // name that the host does not support (a second drive prefix on
            // Windows), or a folder without search permission. A program of the same user
            // cannot open it through this path, so it is not a readable link.
            // A link that exists gives its attributes, also when it dangles or loops.
            return null;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return null;

        return (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).LinkTarget
            : new FileInfo(path).LinkTarget;
    }

    internal static bool IsInspectionFailure(Exception ex)
        => ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private static PathDecision EvaluateFolder(CanonicalPath path, PathBoundary.Folder folder)
    {
        if (!folder.Root.Contains(path))
            return PathDecision.Outside;

        // A path in another host's style stays lexical. The host cannot inspect it.
        if (!path.IsHostStyle)
            return PathDecision.Allowed;

        try
        {
            var crossesLink = folder.Links switch
            {
                LinkRule.BelowRoot => CrossesLink(folder.LinkAnchor.Value, path.Value, includeAnchor: false),
                LinkRule.IncludingRoot => CrossesLink(folder.LinkAnchor.Value, path.Value, includeAnchor: true),
                LinkRule.FromVolumeRoot => CrossesLink(path.VolumeRoot.Value, path.Value, includeAnchor: true),
                LinkRule.FromVolumeRootExceptTemporaryAlias =>
                    !IsBelowTemporaryAlias(path)
                    && CrossesLink(path.VolumeRoot.Value, path.Value, includeAnchor: true),
                _ => throw new InvalidOperationException($"Unknown link rule {folder.Links}.")
            };
            return crossesLink ? PathDecision.CrossesLink : PathDecision.Allowed;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return PathDecision.Unverifiable;
        }
    }

    private static PathDecision EvaluateExactFile(CanonicalPath path, PathBoundary.ExactFile file)
    {
        if (!file.File.IsSamePath(path))
            return PathDecision.Outside;

        try
        {
            return CrossesLink(file.LinkAnchor.Value, path.Value, includeAnchor: true)
                ? PathDecision.CrossesLink
                : PathDecision.Allowed;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return PathDecision.Unverifiable;
        }
    }

    /// <summary>
    /// The single link walker. It returns true when a segment from
    /// <paramref name="anchor"/> to <paramref name="path"/> is a reparse point
    /// (symbolic link, junction, or other). An unreadable segment counts as a link.
    /// </summary>
    internal static bool CrossesLink(string anchor, string path, bool includeAnchor)
    {
        if (includeAnchor && IsLinkOrUnreadable(anchor))
            return true;

        var relativePath = Path.GetRelativePath(anchor, path);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
            return false;

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = anchor;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (IsLinkOrUnreadable(current))
                return true;
        }

        return false;
    }

    private static bool IsLinkOrUnreadable(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static HashSet<string> BuildProtectedSet(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalized = PathUtility.Normalize(path);
            set.Add(normalized);

            // macOS keeps /etc, /var, and /tmp as links into /private. A candidate
            // that resolves to /private/etc must still meet a /etc entry.
            if (TryResolveForProtectedSet(normalized, out var resolved))
                set.Add(resolved);
        }

        return set;
    }

    // Construction skips a resolution failure (R13). A startup throw must not stop
    // the process, and the lexical entry still denies exact and child matches. The
    // evaluation path (IsProtected) fails closed on the same failure.
    private static bool TryResolveForProtectedSet(string path, out string resolved)
    {
        try
        {
            return TryResolveLinks(path, out resolved);
        }
        catch
        {
            resolved = string.Empty;
            return false;
        }
    }

    private static bool IsInProtectedSet(string candidate, HashSet<string> protectedSet)
    {
        foreach (var protectedPath in protectedSet)
        {
            if (CanonicalPath.IsWithin(candidate, protectedPath, CanonicalPath.HostStyle, ignoreCase: true))
                return true;
        }

        return false;
    }

    // This screen keeps the host resolver. Its failures are part of the screen:
    // a path that the host resolver cannot read counts as protected (R13), and
    // on Windows that includes the drive root. FollowLinkChain reads the final
    // target for grant scopes, where a path without a readable link keeps its
    // lexical scope.
    private static bool TryResolveFinalLink(string path, out string target)
    {
        target = string.Empty;
        FileSystemInfo? info = File.Exists(path)
            ? new FileInfo(path)
            : Directory.Exists(path)
                ? new DirectoryInfo(path)
                : null;
        var resolved = info?.ResolveLinkTarget(returnFinalTarget: true);
        if (resolved is null)
            return false;

        target = PathUtility.Normalize(resolved.FullName);
        return true;
    }
}

/// <summary>A platform temporary root by its authored name and its link-resolved name.</summary>
internal readonly record struct PlatformTemporaryRoot(CanonicalPath Authored, CanonicalPath Resolved)
{
    /// <summary>Resolves each existing host temporary directory. It skips a root it cannot resolve.</summary>
    internal static IReadOnlyList<PlatformTemporaryRoot> ResolveAll(IEnumerable<string> candidates)
    {
        var roots = new List<PlatformTemporaryRoot>();
        foreach (var candidate in candidates)
        {
            if (TryResolve(candidate, CanonicalPath.HostStyle, out var root)
                && !roots.Any(existing => existing.Authored.IsSamePath(root.Authored)))
            {
                roots.Add(root);
            }
        }

        return roots.AsReadOnly();
    }

    /// <summary>Resolves one existing temporary directory in the host style.</summary>
    internal static bool TryResolve(string path, ShellPathStyle style, out PlatformTemporaryRoot root)
    {
        root = default;
        if (!CanonicalPath.IsHostPathStyle(style)
            || !CanonicalPath.TryCreate(path, null, style, out var authored)
            || !Directory.Exists(authored.Value))
        {
            return false;
        }

        try
        {
            FileSystemAuthority.TryResolveLinks(authored.Value, out var resolvedText);
            if (!CanonicalPath.TryCreate(resolvedText, null, style, out var resolved))
                return false;

            root = new PlatformTemporaryRoot(authored, resolved);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Maps a path below the authored or resolved root to its resolved form.</summary>
    internal bool TryMapToResolved(CanonicalPath path, out CanonicalPath resolved)
    {
        resolved = default;
        if (Resolved.Contains(path))
        {
            resolved = path;
            return true;
        }

        if (!Authored.Contains(path))
            return false;

        var relative = path.Value[Authored.Value.Length..].TrimStart('/', '\\');
        if (relative.Length == 0)
        {
            resolved = Resolved;
            return true;
        }

        var separator = Resolved.Style == ShellPathStyle.Windows ? '\\' : '/';
        return CanonicalPath.TryCreate(
            Resolved.Value.TrimEnd('/', '\\') + separator + relative,
            relativeBase: null,
            Resolved.Style,
            out resolved);
    }
}
