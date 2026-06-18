// -----------------------------------------------------------------------
// <copyright file="TestNetworkHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Netclaw.Cli.Tests.Cli;

internal static class TestNetworkHelpers
{
    /// <summary>
    /// Reads the port a started host actually bound to. Pair this with Kestrel bound
    /// to <c>http://127.0.0.1:0</c> so the OS assigns a free port and the host holds
    /// it for its whole lifetime.
    ///
    /// This deliberately replaces the old "open a listener on port 0, read the port,
    /// close the listener, hand back the bare number" helper. That pattern is a
    /// time-of-check/time-of-use race: the port is released before the caller binds
    /// Kestrel to it, so under parallel tests another binder can grab the same
    /// ephemeral port in the gap and the loser fails with EADDRINUSE
    /// ("address already in use"). Binding :0 and reading the assigned port back
    /// closes that window entirely — the port is never selected-but-unbound.
    /// </summary>
    public static int GetBoundPort(IHost host)
    {
        var addresses = host.Services.GetRequiredService<IServer>()
                            .Features.Get<IServerAddressesFeature>()
                        ?? throw new InvalidOperationException(
                            "Host exposes no IServerAddressesFeature; call GetBoundPort only after StartAsync.");

        return new Uri(addresses.Addresses.Single()).Port;
    }
}
