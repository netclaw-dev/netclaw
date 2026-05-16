// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Security;

/// <summary>
/// Evaluates whether a file path is denied for agent tool access.
/// </summary>
/// <remarks>
/// Three independent deny surfaces: write (<see cref="IsDenied"/>), read
/// (<see cref="IsReadDenied"/>), and shell indicators
/// (<see cref="CommandReferencesDeniedPath"/>). The shell indicator list must
/// stay narrow — file-level only — because that path does a raw substring scan
/// against the command text, so directory-scoped entries would block legitimate
/// commands whose arguments happen to contain the directory name.
/// </remarks>
public sealed class ToolPathPolicy
{
    private readonly HashSet<string> _writeDeniedPaths;
    private readonly HashSet<string> _readDeniedPaths;
    private readonly HashSet<string> _shellDeniedPaths;
    private readonly HashSet<string> _commandIndicators;

    public ToolPathPolicy(IEnumerable<string> deniedPaths)
    {
        var materialized = deniedPaths.ToList();
        _writeDeniedPaths = BuildNormalizedSet(materialized);
        _readDeniedPaths = _writeDeniedPaths;
        _shellDeniedPaths = _writeDeniedPaths;
        _commandIndicators = BuildCommandIndicators(materialized);
    }

    public ToolPathPolicy(
        IEnumerable<string> writeDeniedPaths,
        IEnumerable<string> readDeniedPaths,
        IEnumerable<string> shellIndicatorPaths)
    {
        _writeDeniedPaths = BuildNormalizedSet(writeDeniedPaths);
        _readDeniedPaths = BuildNormalizedSet(readDeniedPaths);
        var shellList = shellIndicatorPaths.ToList();
        _shellDeniedPaths = BuildNormalizedSet(shellList);
        _commandIndicators = BuildCommandIndicators(shellList);
    }

    private static HashSet<string> BuildNormalizedSet(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalized = PathUtility.Normalize(path);
            set.Add(normalized);

            // macOS keeps /etc, /var, /tmp as symlinks into /private, so a denied
            // path that traverses one resolves to a different real path. The deny
            // checks canonicalize the *candidate* path (TryResolveSymlinksInPath /
            // TryResolveSymlinkTarget); without the resolved denied form here, a
            // candidate resolving to /private/etc/... would slip past a /etc deny.
            if (TryResolveSymlinksInPath(normalized, out var canonical))
                set.Add(canonical);
        }

        return set;
    }

    private static HashSet<string> BuildCommandIndicators(IEnumerable<string> paths)
    {
        var materialized = paths.ToList();
        var normalizedPaths = materialized.Select(PathUtility.Normalize).ToList();
        var indicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in materialized.Concat(normalizedPaths))
        {
            var slashPath = path.Replace('\\', '/');
            indicators.Add(slashPath);

            var netclawSegmentIdx = slashPath.IndexOf("/.netclaw/", StringComparison.OrdinalIgnoreCase);
            if (netclawSegmentIdx >= 0)
                indicators.Add(slashPath[(netclawSegmentIdx + 1)..]);

            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains('.', StringComparison.Ordinal))
                indicators.Add(fileName);
        }

        return indicators;
    }

    /// <summary>
    /// Returns true if the given path is denied for write by policy.
    /// Normalizes the path (resolves "..", removes trailing separators) before checking.
    /// </summary>
    public bool IsDenied(string path)
        => IsDeniedAgainst(path, _writeDeniedPaths);

    /// <summary>
    /// Returns true if the given path is denied for read by policy. Narrower
    /// than <see cref="IsDenied"/>: only covers files that leak credentials.
    /// </summary>
    public bool IsReadDenied(string path)
        => IsDeniedAgainst(path, _readDeniedPaths);

    private static bool IsDeniedAgainst(string path, HashSet<string> deniedSet)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (PathUtility.TryNormalize(path, null, out var normalized) && IsDeniedNormalized(normalized, deniedSet))
            return true;

        return TryResolveSymlinkTarget(path, out var resolvedTarget)
            && IsDeniedNormalized(resolvedTarget, deniedSet);
    }

    /// <summary>
    /// Returns true if the given shell command string contains a reference to any denied path.
    /// Checks both the original path strings and their normalized forms.
    /// This is a defense-in-depth heuristic — not bulletproof against obfuscation.
    /// </summary>
    public bool CommandReferencesDeniedPath(string command, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var slashCommand = command.Replace('\\', '/');
        foreach (var indicator in _commandIndicators)
        {
            if (slashCommand.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var token in tokens)
        {
            if (!LooksLikePath(token))
                continue;

            var normalized = ShellTokenizer.NormalizePathToken(token, workingDirectory);
            if (normalized is not null && IsDeniedNormalized(normalized, _shellDeniedPaths))
            {
                return true;
            }

            // Defense-in-depth against symlink escalation under a directory-
            // scoped approval. Path.GetFullPath (used by NormalizePathToken)
            // collapses `.`/`..` but does NOT resolve symlinks in any path
            // component. Without this pass, a user who approves /home/safe/
            // can be tricked by a planted /home/safe/leak -> /etc symlink:
            // the approval gate sees /home/safe/leak/passwd as "within" the
            // approved root and waves it through, and the static path check
            // here would never see /etc unless we resolve link targets along
            // every component of the path.
            if (normalized is not null
                && TryResolveSymlinksInPath(normalized, out var canonical)
                && IsDeniedNormalized(canonical, _shellDeniedPaths))
            {
                return true;
            }

            var expanded = PathUtility.ExpandHome(token);
            if (expanded.Contains("secrets.json", StringComparison.OrdinalIgnoreCase)
                || expanded.Contains(".netclaw/keys", StringComparison.OrdinalIgnoreCase)
                || expanded.Contains(".netclaw\\keys", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ContainsProtectedPathHint(slashCommand) && ContainsHighRiskVerb(tokens))
            return true;

        return false;
    }

    private static bool ContainsProtectedPathHint(string slashCommand)
    {
        return slashCommand.Contains(".netclaw/config", StringComparison.OrdinalIgnoreCase)
            || slashCommand.Contains(".netclaw/keys", StringComparison.OrdinalIgnoreCase)
            || slashCommand.Contains("secrets.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHighRiskVerb(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(token);
            if (ShellTokenizer.HighRiskVerbs.Contains(verb))
                return true;
        }

        return false;
    }

    private static bool IsDeniedNormalized(string candidate, HashSet<string> deniedSet)
    {
        foreach (var denied in deniedSet)
        {
            if (IsSamePathOrChild(candidate, denied))
                return true;
        }

        return false;
    }

    private static bool IsSamePathOrChild(string candidate, string denied)
    {
        if (!candidate.StartsWith(denied, StringComparison.OrdinalIgnoreCase))
            return false;

        if (candidate.Length == denied.Length)
            return true;

        var boundary = candidate[denied.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static bool TryResolveSymlinksInPath(string path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            // Walk the path component by component, resolving any directory
            // or file symlinks encountered. ResolveLinkTarget(returnFinalTarget:
            // true) follows the chain to a non-link, but only operates on the
            // entity it's invoked against — it does not see symlinks earlier
            // in the path. Hence the explicit segment walk.
            var fullPath = Path.GetFullPath(path);
            var segments = fullPath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            if (Path.IsPathRooted(fullPath))
                sb.Append(Path.DirectorySeparatorChar);

            foreach (var segment in segments)
            {
                if (sb.Length > 0 && sb[^1] != Path.DirectorySeparatorChar)
                    sb.Append(Path.DirectorySeparatorChar);
                sb.Append(segment);

                var partial = sb.ToString();
                if (Directory.Exists(partial))
                {
                    var target = new DirectoryInfo(partial).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        sb.Clear();
                        sb.Append(target.FullName);
                    }
                }
                else if (File.Exists(partial))
                {
                    var target = new FileInfo(partial).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        sb.Clear();
                        sb.Append(target.FullName);
                    }

                    break;
                }
            }

            canonical = PathUtility.Normalize(sb.ToString());
            return !string.IsNullOrEmpty(canonical) && !string.Equals(canonical, PathUtility.Normalize(fullPath), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveSymlinkTarget(string path, out string normalizedTarget)
    {
        normalizedTarget = string.Empty;

        try
        {
            if (File.Exists(path))
            {
                var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                    return false;

                normalizedTarget = PathUtility.Normalize(target.FullName);
                return true;
            }

            if (Directory.Exists(path))
            {
                var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                    return false;

                normalizedTarget = PathUtility.Normalize(target.FullName);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.StartsWith("-", StringComparison.Ordinal))
            return false;

        return token.Contains('/', StringComparison.Ordinal)
            || token.Contains('\\', StringComparison.Ordinal)
            || token.StartsWith(".", StringComparison.Ordinal)
            || token.StartsWith("~", StringComparison.Ordinal)
            || token.Contains(':', StringComparison.Ordinal)
            || token.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

}
