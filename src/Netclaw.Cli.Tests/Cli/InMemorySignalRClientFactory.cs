// -----------------------------------------------------------------------
// <copyright file="InMemorySignalRClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Creates a real SignalR client that uses an in-memory test server.
/// This keeps protocol coverage without a loopback port or a socket race.
/// </summary>
internal static class InMemorySignalRClientFactory
{
    private static readonly TimeSpan[] ImmediateReconnect = [TimeSpan.Zero];

    public static DaemonClient Create(IHost host)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/session", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => host.GetTestServer().CreateHandler();
            })
            .Build();

        return new DaemonClient(
            "http://localhost",
            SignalRDaemonHubTransport.FromConnection(connection),
            reconnectDelays: ImmediateReconnect);
    }
}
