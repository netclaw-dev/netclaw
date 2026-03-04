namespace Netclaw.Security;

/// <summary>
/// Evaluates whether a file path is denied for agent tool access.
/// Used to prevent the LLM from reading/writing sensitive files like secrets.json.
/// </summary>
public sealed class ToolPathPolicy
{
    private readonly HashSet<string> _deniedPaths;
    private readonly HashSet<string> _commandIndicators;
    private static readonly HashSet<string> HighRiskVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "less", "more", "head", "tail", "grep", "rg", "find", "jq", "awk", "sed", "strings", "xxd", "hexdump",
        "cp", "mv", "tar", "zip", "unzip", "scp", "rsync", "curl", "wget", "nc", "ncat",
        "python", "python3", "node", "ruby", "perl", "php"
    };

    public ToolPathPolicy(IEnumerable<string> deniedPaths)
    {
        var paths = deniedPaths.ToList();
        var normalizedPaths = paths.Select(NormalizePath).ToList();

        _deniedPaths = new HashSet<string>(normalizedPaths, StringComparer.OrdinalIgnoreCase);
        _commandIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.Concat(normalizedPaths))
        {
            var slashPath = path.Replace('\\', '/');
            _commandIndicators.Add(slashPath);

            var netclawSegmentIdx = slashPath.IndexOf("/.netclaw/", StringComparison.OrdinalIgnoreCase);
            if (netclawSegmentIdx >= 0)
                _commandIndicators.Add(slashPath[(netclawSegmentIdx + 1)..]);

            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains('.', StringComparison.Ordinal))
                _commandIndicators.Add(fileName);
        }
    }

    /// <summary>
    /// Returns true if the given path is denied by policy.
    /// Normalizes the path (resolves "..", removes trailing separators) before checking.
    /// </summary>
    public bool IsDenied(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (TryNormalizePath(path, null, out var normalized) && IsDeniedNormalized(normalized))
            return true;

        return TryResolveSymlinkTarget(path, out var resolvedTarget)
            && IsDeniedNormalized(resolvedTarget);
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

        var tokens = Tokenize(command).ToList();
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
                && IsDeniedNormalized(normalized))
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
            var verb = TrimShellPunctuation(token);
            if (HighRiskVerbs.Contains(verb))
                return true;
        }

        return false;
    }

    private static string TrimShellPunctuation(string token)
    {
        return token.Trim().TrimStart(';', '|', '&').TrimEnd(';', '|', '&');
    }

    private bool IsDeniedNormalized(string candidate)
    {
        foreach (var denied in _deniedPaths)
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

    private static IEnumerable<string> Tokenize(string command)
    {
        var current = new System.Text.StringBuilder();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is null && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                continue;
            }

            if (quote is not null && ch == quote)
            {
                quote = null;
                continue;
            }

            if (quote is null && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
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
