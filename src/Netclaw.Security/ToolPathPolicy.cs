// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        var normalized = paths.Select(NormalizePath);
        return new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildCommandIndicators(IEnumerable<string> paths)
    {
        var materialized = paths.ToList();
        var normalizedPaths = materialized.Select(NormalizePath).ToList();
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

        if (TryNormalizePath(path, null, out var normalized) && IsDeniedNormalized(normalized, deniedSet))
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

            var expanded = ExpandHomeAndEnv(token);
            if (TryNormalizePath(expanded, workingDirectory, out var normalized)
                && IsDeniedNormalized(normalized, _shellDeniedPaths))
            {
                return true;
            }

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

    private static bool TryNormalizePath(string rawPath, string? workingDirectory, out string normalized)
    {
        normalized = string.Empty;

        try
        {
            var baseDir = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Environment.CurrentDirectory;

            normalized = Path.IsPathRooted(rawPath)
                ? NormalizePath(rawPath)
                : NormalizePath(Path.Combine(baseDir, rawPath));

            return true;
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

                normalizedTarget = NormalizePath(target.FullName);
                return true;
            }

            if (Directory.Exists(path))
            {
                var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                    return false;

                normalizedTarget = NormalizePath(target.FullName);
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

    private static string ExpandHomeAndEnv(string token)
    {
        var expanded = token;

        if (expanded.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                expanded = expanded.Length == 1
                    ? home
                    : Path.Combine(home, expanded[1..].TrimStart('/', '\\'));
            }
        }

        var homeEnv = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeEnv))
        {
            expanded = expanded.Replace("$HOME", homeEnv, StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace("${HOME}", homeEnv, StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace("%USERPROFILE%", homeEnv, StringComparison.OrdinalIgnoreCase);
        }

        return expanded;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
