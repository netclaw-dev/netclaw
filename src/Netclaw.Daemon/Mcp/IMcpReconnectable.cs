// -----------------------------------------------------------------------
// <copyright file="IMcpReconnectable.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal interface IMcpReconnectable
{
    IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses();

    Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default);
}
