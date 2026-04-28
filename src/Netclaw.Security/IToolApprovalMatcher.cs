// -----------------------------------------------------------------------
// <copyright file="IToolApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Security;

/// <summary>
/// Tool-specific pattern extraction and matching for the approval system.
/// Each tool type can provide its own matcher to define what constitutes
/// an "intent-level" pattern for approval purposes.
/// </summary>
public interface IToolApprovalMatcher
{
    /// <summary>
    /// Returns the key used to look up this invocation's approval mode in
    /// <c>ToolApprovalConfig.ToolOverrides</c>. Most matchers return the tool
    /// name unchanged; argument-aware matchers may return a context-specific
    /// key so different invocations of the same tool (e.g., a write to a
    /// control-plane file vs. a write to a user file) can be gated
    /// independently.
    /// </summary>
    string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns true if this invocation must require interactive approval on
    /// the Personal audience when no explicit approval policy is configured.
    /// Encapsulates the fail-closed decision so callers do not have to inspect
    /// tool names or approval-key string formats.
    /// </summary>
    bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Extracts the intent-level pattern from a tool call's arguments.
    /// For shell: verb-chain prefix (e.g., "git push" from "git push origin main").
    /// For other tools: the tool name itself.
    /// </summary>
    IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Checks if the tool call matches any approved pattern.
    /// </summary>
    bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns);

    /// <summary>
    /// Formats the tool call for display in the approval prompt.
    /// </summary>
    string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Shell-specific approval matcher using verb-chain prefix extraction.
/// Handles compound commands by extracting patterns from each segment.
/// </summary>
public sealed class ShellApprovalMatcher : IToolApprovalMatcher
{
    public static readonly ShellApprovalMatcher Instance = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => true;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPatterns(command, patterns);

        return patterns.ToList();
    }

    public bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
    {
        var commandPatterns = ExtractPatterns(toolName, arguments);
        if (commandPatterns.Count == 0)
            return true; // Empty command, nothing to approve

        var approvedList = approvedPatterns as IReadOnlyList<string> ?? approvedPatterns.ToList();
        foreach (var pattern in commandPatterns)
        {
            if (!PatternMatchesAny(pattern, approvedList))
                return false;
        }

        return true;
    }

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return GetCommand(arguments) ?? "(empty command)";
    }

    private static string? GetCommand(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        if (arguments.TryGetValue("Command", out var val) || arguments.TryGetValue("command", out val))
            return val?.ToString();

        return null;
    }

    private static bool PatternMatchesAny(string pattern, IReadOnlyList<string> approvedPatterns)
        => ApprovalPatternMatching.MatchesAny(pattern, approvedPatterns);

    private static void CollectPatterns(string command, ISet<string> patterns)
    {
        foreach (var segment in ShellTokenizer.SplitCompoundCommand(command))
        {
            var innerCommands = ShellTokenizer.ExtractInnerCommands(segment);
            if (innerCommands.Count > 0)
            {
                foreach (var inner in innerCommands)
                    CollectPatterns(inner, patterns);

                continue;
            }

            var verbChain = ShellTokenizer.ExtractVerbChain(segment);
            if (!string.IsNullOrEmpty(verbChain))
                patterns.Add(verbChain);
        }
    }
}

/// <summary>
/// Default approval matcher for non-shell tools. Approval is at the tool-name
/// level — either the tool is approved or it isn't.
/// </summary>
public sealed class DefaultApprovalMatcher : IToolApprovalMatcher
{
    public static readonly DefaultApprovalMatcher Instance = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return [toolName.Value];
    }

    public bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(toolName.Value, approved, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return toolName.Value;
    }
}
