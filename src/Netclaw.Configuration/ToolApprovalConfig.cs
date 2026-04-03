namespace Netclaw.Configuration;

/// <summary>
/// Approval mode for a tool invocation.
/// </summary>
public enum ToolApprovalMode
{
    /// <summary>No approval required — tool executes immediately.</summary>
    Auto,

    /// <summary>Interactive approval required before execution.</summary>
    Approval,

    /// <summary>Always blocked — equivalent to removing from the allowlist.</summary>
    Deny
}

/// <summary>
/// Per-audience configuration for tool approval gates.
/// Determines which tools require interactive user approval before execution.
/// </summary>
public sealed class ToolApprovalConfig
{
    /// <summary>
    /// Default approval mode for tools not explicitly overridden.
    /// </summary>
    public ToolApprovalMode DefaultMode { get; set; } = ToolApprovalMode.Auto;

    /// <summary>
    /// Per-tool approval mode overrides. Keys are tool names
    /// (e.g., "shell_execute", "mcp:server-name:tool-name", "file_write").
    /// </summary>
    public Dictionary<string, ToolApprovalMode> ToolOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the effective approval mode for a tool, checking overrides first.
    /// </summary>
    public ToolApprovalMode GetEffectiveMode(string toolName)
    {
        return ToolOverrides.TryGetValue(toolName, out var mode) ? mode : DefaultMode;
    }
}
