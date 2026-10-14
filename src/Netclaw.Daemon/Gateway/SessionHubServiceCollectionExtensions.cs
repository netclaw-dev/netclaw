// -----------------------------------------------------------------------
// <copyright file="SessionHubServiceCollectionExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

public static class SessionHubServiceCollectionExtensions
{
    /// <summary>
    /// Registers SignalR with the session hub's frame size limit. The daemon and tests both call this
    /// so the limit cannot drift between production and test hosts.
    /// </summary>
    public static IServiceCollection AddSessionHubSignalR(this IServiceCollection services)
    {
        services.AddSignalR(options => options.MaximumReceiveMessageSize = SessionHubLimits.MaxReceiveMessageBytes);
        return services;
    }
}
