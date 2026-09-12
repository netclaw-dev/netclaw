// -----------------------------------------------------------------------
// <copyright file="DailyStatsHostingExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.DependencyInjection;
using Akka.Hosting;
using Netclaw.Actors.Hosting;

namespace Netclaw.Daemon.Gateway;

public static class DailyStatsHostingExtensions
{
    public static AkkaConfigurationBuilder WithDailyStatsActor(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(
                resolver.Props<DailyStatsActor>(),
                "daily-stats");
            registry.Register<DailyStatsActorKey>(actor);
        });
    }
}
