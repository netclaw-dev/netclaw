namespace Netclaw.Security;

/// <summary>
/// Tool-specific pattern extraction and matching for the approval system.
/// Each tool type can provide its own matcher to define what constitutes
/// an "intent-level" pattern for approval purposes.
/// </summary>
public interface IToolApprovalMatcher
{
    /// <summary>
    /// Extracts the intent-level pattern from a tool call's arguments.
    /// For shell: verb-chain prefix (e.g., "git push" from "git push origin main").
    /// For other tools: the tool name itself.
    /// </summary>
    IReadOnlyList<string> ExtractPatterns(string toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Checks if the tool call matches any approved pattern.
    /// </summary>
    bool IsApproved(string toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns);

    /// <summary>
    /// Formats the tool call for display in the approval prompt.
    /// </summary>
    string FormatForDisplay(string toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Shell-specific approval matcher using verb-chain prefix extraction.
/// Handles compound commands by extracting patterns from each segment.
/// </summary>
public sealed class ShellApprovalMatcher : IToolApprovalMatcher
{
    public static readonly ShellApprovalMatcher Instance = new();

    public IReadOnlyList<string> ExtractPatterns(string toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var segments = ShellTokenizer.SplitCompoundCommand(command);
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            var verbChain = ShellTokenizer.ExtractVerbChain(segment);
            if (!string.IsNullOrEmpty(verbChain))
                patterns.Add(verbChain);
        }

        return patterns.ToList();
    }

    public bool IsApproved(string toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
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

    public string FormatForDisplay(string toolName, IDictionary<string, object?>? arguments)
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
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(pattern, approved, StringComparison.OrdinalIgnoreCase))
                return true;

            // Approved "git" matches pattern "git push" (prefix)
            if (pattern.StartsWith(approved, StringComparison.OrdinalIgnoreCase)
                && pattern.Length > approved.Length
                && pattern[approved.Length] == ' ')
                return true;
        }

        return false;
    }
}

/// <summary>
/// Default approval matcher for non-shell tools. Approval is at the tool-name
/// level — either the tool is approved or it isn't.
/// </summary>
public sealed class DefaultApprovalMatcher : IToolApprovalMatcher
{
    public static readonly DefaultApprovalMatcher Instance = new();

    public IReadOnlyList<string> ExtractPatterns(string toolName, IDictionary<string, object?>? arguments)
    {
        return [toolName];
    }

    public bool IsApproved(string toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(toolName, approved, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string FormatForDisplay(string toolName, IDictionary<string, object?>? arguments)
    {
        return toolName;
    }
}
