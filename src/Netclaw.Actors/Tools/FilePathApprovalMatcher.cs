using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Argument-aware approval matcher for the <c>file_write</c> and <c>file_edit</c>
/// tools. Inspects the target path and, when it lands inside Netclaw's control
/// plane (<c>~/.netclaw/config/</c>), routes the invocation to a distinct
/// approval-mode key so operators can gate control-plane writes without
/// requiring approval for every ordinary file write.
/// </summary>
/// <remarks>
/// Tier split: Tier 1 files (secrets, keys, SQLite DB, pid/lock, restart
/// manifest) are hard-denied earlier by <see cref="ToolPathPolicy.IsDenied"/>
/// and never reach this matcher. Tier 2 files — the rest of
/// <c>~/.netclaw/config/</c> — are routed here and surfaced through the
/// standard approval flow with per-path patterns so approving
/// <c>netclaw.json</c> does not implicitly approve <c>tool-approvals.json</c>.
/// </remarks>
public sealed class FilePathApprovalMatcher : IToolApprovalMatcher
{
    public const string ControlPlaneModeKeySuffix = ":control-plane";
    public const string ControlPlanePatternPrefix = "control-plane:";

    private readonly string _controlPlaneRoot;

    public FilePathApprovalMatcher(string controlPlaneRoot)
    {
        _controlPlaneRoot = NormalizePath(controlPlaneRoot);
    }

    public string GetApprovalModeKey(string toolName, IDictionary<string, object?>? arguments)
    {
        return TryGetControlPlaneRelativePath(arguments, out _)
            ? toolName + ControlPlaneModeKeySuffix
            : toolName;
    }

    public IReadOnlyList<string> ExtractPatterns(string toolName, IDictionary<string, object?>? arguments)
    {
        if (TryGetControlPlaneRelativePath(arguments, out var relativePath))
            return [toolName + ControlPlaneModeKeySuffix + ":" + relativePath];

        return [toolName];
    }

    public bool IsApproved(string toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
    {
        var patterns = ExtractPatterns(toolName, arguments);
        foreach (var pattern in patterns)
        {
            var matched = false;
            foreach (var approved in approvedPatterns)
            {
                if (string.Equals(pattern, approved, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                return false;
        }

        return true;
    }

    public string FormatForDisplay(string toolName, IDictionary<string, object?>? arguments)
    {
        if (TryGetPath(arguments, out var path))
            return $"{toolName}: {path}";

        return toolName;
    }

    private bool TryGetControlPlaneRelativePath(
        IDictionary<string, object?>? arguments,
        out string relativePath)
    {
        relativePath = string.Empty;

        if (!TryGetPath(arguments, out var rawPath))
            return false;

        if (!TryNormalizePath(rawPath, out var normalized))
            return false;

        if (!IsUnderRoot(normalized, _controlPlaneRoot))
            return false;

        relativePath = Path.GetRelativePath(_controlPlaneRoot, normalized)
            .Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    private static bool TryGetPath(IDictionary<string, object?>? arguments, out string path)
    {
        path = string.Empty;
        if (arguments is null)
            return false;

        if (arguments.TryGetValue("Path", out var value) || arguments.TryGetValue("path", out value))
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                path = s;
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalizePath(string rawPath, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var baseDir = Environment.CurrentDirectory;
            var combined = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(baseDir, rawPath);
            normalized = NormalizePath(combined);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnderRoot(string candidate, string root)
    {
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        if (candidate.Length == root.Length)
            return true;

        var boundary = candidate[root.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }
}
