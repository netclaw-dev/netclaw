// -----------------------------------------------------------------------
// <copyright file="FilePathApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Argument-aware approval matcher for the <c>file_write</c> and <c>file_edit</c>
/// tools. Routes writes under a configured control-plane root to a distinct
/// approval-mode key so those invocations can be gated without requiring
/// approval for every ordinary file write.
/// </summary>
public sealed class FilePathApprovalMatcher : IToolApprovalMatcher
{
    public const string ControlPlaneModeKeySuffix = ":control-plane";

    private readonly string _controlPlaneRoot;

    public FilePathApprovalMatcher(string controlPlaneRoot)
    {
        _controlPlaneRoot = PathUtility.Normalize(controlPlaneRoot);
    }

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return TryGetControlPlaneRelativePath(arguments, out _)
            ? toolName.Value + ControlPlaneModeKeySuffix
            : toolName.Value;
    }

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => TryGetControlPlaneRelativePath(arguments, out _);

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        if (TryGetControlPlaneRelativePath(arguments, out var relativePath))
            return [toolName.Value + ControlPlaneModeKeySuffix + ":" + relativePath];

        return [toolName.Value];
    }

    public bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
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

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        if (TryGetPath(arguments, out var path))
            return $"{toolName.Value}: {path}";

        return toolName.Value;
    }

    private bool TryGetControlPlaneRelativePath(
        IDictionary<string, object?>? arguments,
        out string relativePath)
    {
        relativePath = string.Empty;

        if (!TryGetPath(arguments, out var rawPath))
            return false;

        if (!PathUtility.TryNormalize(rawPath, out var normalized))
            return false;

        if (!PathUtility.IsWithinRoot(normalized, _controlPlaneRoot))
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

}
