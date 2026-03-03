namespace Netclaw.Security;

/// <summary>
/// Evaluates whether a file path is denied for agent tool access.
/// Used to prevent the LLM from reading/writing sensitive files like secrets.json.
/// </summary>
public sealed class ToolPathPolicy
{
    private readonly HashSet<string> _deniedPaths;
    // Keep both raw and normalized forms for command substring matching.
    // On Windows, Path.GetFullPath("/home/user/...") normalizes to "D:\home\user\...",
    // but the command string may contain the original Unix-style path.
    private readonly HashSet<string> _deniedPathStrings;

    public ToolPathPolicy(IEnumerable<string> deniedPaths)
    {
        var paths = deniedPaths.ToList();
        _deniedPaths = new HashSet<string>(
            paths.Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        _deniedPathStrings = new HashSet<string>(
            paths.Concat(paths.Select(NormalizePath)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the given path is denied by policy.
    /// Normalizes the path (resolves "..", removes trailing separators) before checking.
    /// </summary>
    public bool IsDenied(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = NormalizePath(path);
        return _deniedPaths.Contains(normalized);
    }

    /// <summary>
    /// Returns true if the given shell command string contains a reference to any denied path.
    /// Checks both the original path strings and their normalized forms.
    /// This is a defense-in-depth heuristic — not bulletproof against obfuscation.
    /// </summary>
    public bool CommandReferencesDeniedPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        foreach (var deniedPath in _deniedPathStrings)
        {
            if (command.Contains(deniedPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
