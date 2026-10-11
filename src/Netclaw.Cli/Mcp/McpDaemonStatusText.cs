// -----------------------------------------------------------------------
// <copyright file="McpDaemonStatusText.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Cli.Mcp;

/// <summary>
/// Renders a connected MCP server's entry from <c>/api/mcp/statuses</c> the same way for
/// <c>netclaw mcp list</c> and <c>netclaw doctor</c>. Whether a server is degraded is
/// decided by the daemon and read from the <c>degraded</c> field, never re-derived here.
/// </summary>
internal static class McpDaemonStatusText
{
    public static bool IsDegraded(JsonElement entry)
        => entry.TryGetProperty("degraded", out var degraded) && degraded.ValueKind is JsonValueKind.True;

    public static string FormatConnected(JsonElement entry, int toolCount, string? error)
        => IsDegraded(entry)
            ? $"connected but not responding ({toolCount} cached tools) — {error ?? "catalog refresh failing"}"
            : $"connected ({toolCount} tools)";
}
