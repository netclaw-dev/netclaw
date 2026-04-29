// -----------------------------------------------------------------------
// <copyright file="ChannelRegistrationHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Channels;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Shared registration helpers for channel integrations. Each channel uses a
/// keyed singleton so it can be resolved by name, then forwards the keyed
/// registration to the non-keyed <see cref="IChannel"/> and
/// <see cref="IHostedService"/> collections.
/// </summary>
internal static class ChannelRegistrationHelpers
{
    /// <summary>
    /// Registers a channel implementation as a keyed singleton, forwards it to
    /// the non-keyed <see cref="IChannel"/> collection, and registers it as an
    /// <see cref="IHostedService"/> so the host starts/stops it automatically.
    /// </summary>
    internal static void AddChannelSingleton<TChannel>(
        this IServiceCollection services, string channelKey)
        where TChannel : class, IChannel
    {
        services.AddKeyedSingleton<IChannel, TChannel>(channelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(channelKey));
        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(channelKey));
    }
}
