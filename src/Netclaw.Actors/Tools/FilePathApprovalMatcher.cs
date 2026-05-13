// -----------------------------------------------------------------------
// <copyright file="FilePathApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
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

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractPatterns(toolName, arguments);

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractCandidateVerbs(toolName, arguments)
            .Select(v => new ApprovalCandidate(v, Directory: null))
            .ToList();

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
    {
        // Fail-closed when no verbs can be extracted: an empty foreach
        // would otherwise fall through to "approved" purely because there
        // was nothing to check.
        var verbs = ExtractCandidateVerbs(toolName, arguments);
        if (verbs.Count == 0)
            return false;

        foreach (var verb in verbs)
        {
            if (!ApprovalPatternMatching.MatchesAny(verb, approvedEntries))
                return false;
        }

        return true;
    }

    public bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

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
        // Route through ToolArgumentHelper.GetString so JsonElement-shaped
        // arguments (the form LLM-generated tool calls arrive in) get
        // string-converted correctly. The direct `is string` pattern
        // previously here silently returned false for every JsonElement
        // value — which made GetApprovalModeKey return the non-control-plane
        // key for control-plane writes and IsFailClosedOnPersonal fail
        // open. ExtractShellCommand was fixed for the same shape in this
        // PR; this matcher mirrors that fix.
        var raw = ToolArgumentHelper.GetString(arguments, "Path");
        if (string.IsNullOrWhiteSpace(raw))
        {
            path = string.Empty;
            return false;
        }

        path = raw;
        return true;
    }

}
