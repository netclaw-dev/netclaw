// -----------------------------------------------------------------------
// <copyright file="RepositoryIdentity.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security.Authorization.Filesystem;

/// <summary>
/// The Git repository that owns a directory. The identity is the Git common
/// directory. A linked worktree counts only with reciprocal worktree metadata; a
/// <c>.git</c> pointer alone does not register a worktree.
/// </summary>
/// <remarks>
/// <see cref="TryResolve"/> reads Git metadata on every call and caches nothing
/// (R9). A cached identity would accept a worktree registration that changed after
/// the prompt. The resolver takes raw text so that it can refuse a <c>..</c> segment
/// before normalization (R14). Any link from the volume root to a Git file refuses
/// the identity.
/// </remarks>
internal sealed record RepositoryIdentity(
    string WorktreeRoot,
    string CommonDirectory,
    string ResolvedDirectory)
{
    private const int MaximumPointerBytes = 4096;

    /// <summary>Resolves every directory to one repository, or fails.</summary>
    internal static bool TryResolveAll(
        IReadOnlyList<string?> candidateDirectories,
        string? cwd,
        out IReadOnlyList<RepositoryIdentity>? identities)
    {
        identities = null;
        if (candidateDirectories.Count == 0)
            return false;

        var resolved = new RepositoryIdentity[candidateDirectories.Count];
        for (var index = 0; index < candidateDirectories.Count; index++)
        {
            if (!TryResolve(candidateDirectories[index], cwd, out var identity)
                || identity is null
                || index > 0
                && !PathUtility.AreEquivalentPaths(resolved[0].CommonDirectory, identity.CommonDirectory))
            {
                return false;
            }

            resolved[index] = identity;
        }

        identities = Array.AsReadOnly(resolved);
        return true;
    }

    /// <summary>
    /// Resolves the repository of <paramref name="candidateDirectory"/>, or of
    /// <paramref name="cwd"/> when the candidate is null. A relative candidate
    /// resolves against the cwd after home expansion.
    /// </summary>
    internal static bool TryResolve(
        string? candidateDirectory,
        string? cwd,
        out RepositoryIdentity? identity)
    {
        identity = null;
        if (candidateDirectory is not null
            && (string.IsNullOrWhiteSpace(candidateDirectory)
                || CanonicalPath.HasParentSegment(candidateDirectory)
                || !Path.IsPathFullyQualified(candidateDirectory)
                && (string.IsNullOrWhiteSpace(cwd)
                    || !Path.IsPathFullyQualified(cwd)
                    || CanonicalPath.HasParentSegment(cwd))))
        {
            return false;
        }

        try
        {
            var effectiveDirectory = candidateDirectory is null
                ? cwd
                : PathUtility.ExpandAndNormalize(candidateDirectory, cwd);
            return TryResolveDirectory(effectiveDirectory, out identity);
        }
        catch (Exception ex) when (FileSystemAuthority.IsInspectionFailure(ex))
        {
            return false;
        }
    }

    private static bool TryResolveDirectory(string? cwd, out RepositoryIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(cwd)
            || !Path.IsPathFullyQualified(cwd)
            || CanonicalPath.HasParentSegment(cwd))
        {
            return false;
        }

        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
            if (!Directory.Exists(candidate) || HasLink(candidate))
                return false;

            for (var root = candidate; root is not null; root = Directory.GetParent(root)?.FullName)
            {
                var dotGit = Path.Combine(root, ".git");
                if (Directory.Exists(dotGit))
                {
                    if (!HasLink(dotGit) && IsGitCommonDirectory(dotGit))
                    {
                        identity = new RepositoryIdentity(root, dotGit, candidate);
                        return true;
                    }

                    return false;
                }

                if (!File.Exists(dotGit))
                    continue;

                if (HasLink(dotGit)
                    || !TryReadSmallFile(dotGit, out var pointer)
                    || !pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                {
                    return false;
                }

                var adminPointer = pointer["gitdir: ".Length..];
                if (HasUnsafePointerSegment(adminPointer, root, directoryTarget: true))
                    return false;

                var admin = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(adminPointer, root));
                if (!Directory.Exists(admin) || HasLink(admin)
                    || !TryReadSmallFile(Path.Combine(admin, "commondir"), out var commonPointer)
                    || !TryReadSmallFile(Path.Combine(admin, "gitdir"), out var reversePointer)
                    || !HasValidHead(Path.Combine(admin, "HEAD"))
                    || HasUnsafePointerSegment(commonPointer, admin, directoryTarget: true)
                    || HasUnsafePointerSegment(reversePointer, admin, directoryTarget: false))
                {
                    return false;
                }

                var common = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(commonPointer, admin));
                var reverse = Path.GetFullPath(reversePointer, admin);
                if (!IsGitCommonDirectory(common)
                    || HasLink(common)
                    || !PathUtility.AreEquivalentPaths(reverse, dotGit)
                    || !PathUtility.AreEquivalentPaths(
                        Path.GetDirectoryName(admin)!,
                        Path.Combine(common, "worktrees")))
                {
                    return false;
                }

                identity = new RepositoryIdentity(root, common, candidate);
                return true;
            }
        }
        catch (Exception ex) when (FileSystemAuthority.IsInspectionFailure(ex))
        {
            return false;
        }

        return false;
    }

    private static bool IsGitCommonDirectory(string directory)
        => Directory.Exists(directory)
           && HasValidHead(Path.Combine(directory, "HEAD"))
           && File.Exists(Path.Combine(directory, "config"))
           && Directory.Exists(Path.Combine(directory, "objects"));

    private static bool HasValidHead(string path)
    {
        if (!TryReadSmallFile(path, out var head))
            return false;

        if (head.Length is 40 or 64 && head.All(char.IsAsciiHexDigit))
            return true;

        const string Prefix = "ref: refs/heads/";
        if (!head.StartsWith(Prefix, StringComparison.Ordinal)
            || head.Contains("..", StringComparison.Ordinal)
            || head.Contains("@{", StringComparison.Ordinal)
            || head.EndsWith('.'))
        {
            return false;
        }

        return head["ref: ".Length..].Split('/').All(component =>
            component.Length > 0
            && component[0] != '.'
            && !component.EndsWith(".lock", StringComparison.Ordinal)
            && component.All(character => character is > ' ' and not '\u007f'
                and not '~' and not '^' and not ':' and not '?'
                and not '*' and not '[' and not '\\'));
    }

    private static bool HasLink(string path)
        => FileSystemAuthority.CrossesLink(Path.GetPathRoot(path)!, path, includeAnchor: true);

    /// <summary>
    /// Walks an authored Git pointer and refuses a link, a file in a directory
    /// position, or a target of the wrong kind. The general link walker cannot do
    /// this: it does not know which segment must be a directory.
    /// </summary>
    private static bool HasUnsafePointerSegment(
        string pointer,
        string baseDirectory,
        bool directoryTarget)
    {
        if (!directoryTarget && (pointer.EndsWith(Path.DirectorySeparatorChar)
                                 || pointer.EndsWith(Path.AltDirectorySeparatorChar)))
            return true;

        var authoredPath = Path.IsPathFullyQualified(pointer)
            ? pointer
            : Path.Combine(baseDirectory, pointer);
        var root = Path.GetPathRoot(authoredPath)!;
        var current = root;
        var segments = authoredPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                current = Directory.GetParent(current)?.FullName ?? current;
                continue;
            }

            current = Path.Combine(current, segment);
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || index < segments.Length - 1
                       && (attributes & FileAttributes.Directory) == 0)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                return true;
            }
        }

        try
        {
            var targetIsDirectory =
                (File.GetAttributes(current) & FileAttributes.Directory) != 0;
            return targetIsDirectory != directoryTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool TryReadSmallFile(string path, out string value)
    {
        value = string.Empty;
        if (!File.Exists(path) || HasLink(path) || new FileInfo(path).Length > MaximumPointerBytes)
            return false;

        var content = File.ReadAllText(path);
        value = content.EndsWith("\r\n", StringComparison.Ordinal)
            ? content[..^2]
            : content.EndsWith('\n')
                ? content[..^1]
                : content;
        return value.Length > 0
               && !value.Contains('\n', StringComparison.Ordinal)
               && !value.Contains('\r', StringComparison.Ordinal);
    }
}
