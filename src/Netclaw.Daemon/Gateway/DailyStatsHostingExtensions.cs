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
