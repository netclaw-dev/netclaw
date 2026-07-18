// -----------------------------------------------------------------------
// <copyright file="ToolName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Strongly-typed tool identity. Wraps the tool name string used for
/// approval checks, registry lookups, and access control decisions.
/// For MCP tools the format is typically "{serverName}/{toolName}".
/// </summary>
public readonly record struct ToolName(string Value)
{
    /// <summary>
    /// MCP tools use the canonical operator-facing <c>{server}/{tool}</c>
    /// identity. First-party tool names never contain the separator.
    /// </summary>
    public bool IsMcp => !string.IsNullOrEmpty(Value)
                         && Value.IndexOf('/', StringComparison.Ordinal) > 0;

    public static explicit operator ToolName(string value) => new(value);

    public override string ToString() => Value;
}
