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
    public static explicit operator ToolName(string value) => new(value);

    public override string ToString() => Value;
}
