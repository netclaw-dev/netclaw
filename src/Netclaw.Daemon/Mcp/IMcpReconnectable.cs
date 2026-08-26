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

    /// <summary>
    /// Re-lists a healthy server's tool catalog on its live client. Throttled by the
    /// implementer; returns false when the server is not connected, refreshed too
    /// recently, or the catalog is unchanged.
    /// </summary>
    Task<bool> TryRefreshCatalogAsync(McpServerName serverName, CancellationToken ct = default);
}
