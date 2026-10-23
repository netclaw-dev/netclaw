// -----------------------------------------------------------------------
// <copyright file="FilePathApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Approval matcher for the <c>file_write</c> and <c>file_edit</c> tools. Approval
/// is at the tool-name level, as for any non-shell tool. The prompt text adds the
/// target path so the operator sees which file the call changes.
/// </summary>
/// <remarks>
/// A write under the config directory never reaches this matcher's consent
/// request: path access denies it first, even with approval.
/// </remarks>
public sealed class FilePathApprovalMatcher : IToolApprovalMatcher
{
    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractPatterns(toolName, arguments);

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractCandidateVerbs(toolName, arguments)
            .Select(v => new ApprovalCandidate(v, Directory: null))
            .ToList();

    public bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        // ToolArgumentHelper.GetString converts the JsonElement-shaped arguments
        // that LLM-generated tool calls arrive in.
        var path = ToolArgumentHelper.GetString(arguments, "Path");
        return string.IsNullOrWhiteSpace(path)
            ? toolName.Value
            : $"{toolName.Value}: {path}";
    }
}
