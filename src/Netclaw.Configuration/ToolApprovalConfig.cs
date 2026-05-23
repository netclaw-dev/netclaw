// -----------------------------------------------------------------------
// <copyright file="ToolApprovalConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    /// Per-tool approval mode overrides. Keys are exact tool names
    /// (e.g., "shell_execute", "file_write"). For MCP tools the key format is
    /// "{serverName}/{toolName}" (for example "notion/create-pages"). An exact
    /// entry here always wins over <see cref="McpServerDefaults"/> and
    /// <see cref="DefaultMode"/>.
    /// </summary>
    public Dictionary<string, ToolApprovalMode> ToolOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-MCP-server approval mode defaults. Keys are MCP server names
    /// (e.g., "notion"). Applies to any tool whose name matches
    /// "{serverName}/*" unless an exact entry in <see cref="ToolOverrides"/>
    /// takes precedence. Tools discovered on the server after config was
    /// written inherit this default automatically.
    /// </summary>
    public Dictionary<string, ToolApprovalMode> McpServerDefaults { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the effective approval mode for a tool using the three-step
    /// precedence: exact <see cref="ToolOverrides"/> entry →
    /// <see cref="McpServerDefaults"/> entry (when the tool name is an MCP
    /// tool of the form "{serverName}/{toolName}") → <see cref="DefaultMode"/>.
    /// </summary>
    public ToolApprovalMode GetEffectiveMode(string toolName)
    {
        return TryGetExplicitMode(toolName, out var mode) ? mode : DefaultMode;
    }

    /// <summary>
    /// Checks whether the tool has an explicit entry in either
    /// <see cref="ToolOverrides"/> (exact match) or
    /// <see cref="McpServerDefaults"/> (by server prefix). This is the
    /// shared precedence logic used by both <see cref="GetEffectiveMode"/>
    /// and the runtime <c>ToolAccessPolicy.ResolveApprovalMode</c> path so
    /// the two callers cannot drift.
    /// </summary>
    public bool TryGetExplicitMode(string toolName, out ToolApprovalMode mode)
    {
        if (ToolOverrides.TryGetValue(toolName, out mode))
        {
            return true;
        }

        // PR follow-up: accept either canonical (server/tool) or
        // LLM-facing (server__tool) keys in ToolOverrides. The runtime
        // queries with canonical, but operators may have written the
        // LLM-facing form they saw in audit logs / transcripts before
        // the operator/LLM-audience split was tightened. Convention:
        // first-party tool names never contain '__', so a '__' in the
        // queried canonical name unambiguously maps to an MCP alias
        // and vice versa.
        var slashIndex = toolName.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex > 0)
        {
            var aliasKey = toolName.Replace("/", "__", StringComparison.Ordinal);
            if (ToolOverrides.TryGetValue(aliasKey, out mode))
            {
                return true;
            }

            var serverName = toolName[..slashIndex];
            if (McpServerDefaults.TryGetValue(serverName, out mode))
            {
                return true;
            }
        }

        mode = default;
        return false;
    }
}
