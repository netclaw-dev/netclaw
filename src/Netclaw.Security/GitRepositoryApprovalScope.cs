// -----------------------------------------------------------------------
// <copyright file="GitRepositoryApprovalScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// A repository grant uses reciprocal Git worktree metadata as its authority.
/// A .git pointer alone does not register a worktree.
/// </summary>
internal sealed record GitRepositoryApprovalScope(
    string WorktreeRoot,
    string CommonDirectory,
    string ResolvedDirectory)
{
    private const int MaximumPointerBytes = 4096;

    internal static bool TryResolveCandidates(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        out IReadOnlyList<GitRepositoryApprovalScope>? scopes)
    {
        scopes = null;
        if (candidates.Count == 0)
            return false;

        var resolved = new GitRepositoryApprovalScope[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!TryResolveCandidate(candidates[index].Directory, cwd, out var scope)
                || scope is null
                || index > 0
                && !PathUtility.AreEquivalentPaths(resolved[0].CommonDirectory, scope.CommonDirectory))
            {
                return false;
            }

            resolved[index] = scope;
        }

        scopes = Array.AsReadOnly(resolved);
        return true;
    }

    internal static bool TryResolveCandidate(
        string? candidateDirectory,
        string? cwd,
        out GitRepositoryApprovalScope? scope)
    {
        scope = null;
        if (candidateDirectory is not null
            && (string.IsNullOrWhiteSpace(candidateDirectory)
                || ShellPathRules.HasParentDirectorySegment(candidateDirectory)
                || !Path.IsPathFullyQualified(candidateDirectory)
                && (string.IsNullOrWhiteSpace(cwd)
                    || !Path.IsPathFullyQualified(cwd)
                    || ShellPathRules.HasParentDirectorySegment(cwd))))
        {
            return false;
        }

        try
        {
            var effectiveDirectory = candidateDirectory is null
                ? cwd
                : PathUtility.ExpandAndNormalize(candidateDirectory, cwd);
            return TryResolve(effectiveDirectory, out scope);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                   or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool TryResolve(string? cwd, out GitRepositoryApprovalScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(cwd)
            || !Path.IsPathFullyQualified(cwd)
            || ShellPathRules.HasParentDirectorySegment(cwd))
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
                        scope = new GitRepositoryApprovalScope(root, dotGit, candidate);
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

                scope = new GitRepositoryApprovalScope(root, common, candidate);
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                   or UnauthorizedAccessException or System.Security.SecurityException)
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
        => PathUtility.ContainsSymlinkSegment(Path.GetPathRoot(path)!, path, includeRoot: true);

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
