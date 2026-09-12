// -----------------------------------------------------------------------
// <copyright file="McpServerName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Strongly-typed MCP server identity. Wraps the server name string used
/// for client lifecycle management, OAuth flows, and access control.
/// </summary>
public readonly record struct McpServerName(string Value)
{
    public static explicit operator McpServerName(string value) => new(value);

    public override string ToString() => Value;
}
