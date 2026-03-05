using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Routing;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Hosting;

public static class NetclawAkkaHostingExtensions
{
    /// <summary>
    /// Registers the session manager as a <see cref="GenericChildPerEntityParent"/>
    /// that routes <see cref="Protocol.IWithSessionId"/> messages to per-session
    /// <see cref="LlmSessionActor"/> children.
    /// </summary>
    public static AkkaConfigurationBuilder WithSessionManager(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var sessionManager = system.ActorOf(
                GenericChildPerEntityParent.CreateProps(
                    new SessionMessageExtractor(),
                    entityId => resolver.Props<LlmSessionActor>(entityId)),
                "session-manager");
            registry.Register<SessionManagerActorKey>(sessionManager);
        });
    }

    /// <summary>
    /// Registers the model capability cache as a singleton actor.
    /// Requires <see cref="Netclaw.Configuration.IModelCapabilityResolver"/>
    /// to be registered in DI.
    /// </summary>
    public static AkkaConfigurationBuilder WithModelCapabilityCache(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var capabilityActor = system.ActorOf(
                resolver.Props<ModelCapabilityActor>(),
                "model-capabilities");
            registry.Register<ModelCapabilityActorKey>(capabilityActor);
        });
    }

    /// <summary>
    /// Registers the reminder manager as a singleton actor and wires
    /// the local akka-reminders scheduler to deliver payloads to it.
    /// Uses in-memory storage (reminders do not survive daemon restarts).
    /// </summary>
    public static AkkaConfigurationBuilder WithReminderManager(
        this AkkaConfigurationBuilder builder)
    {
        // Shared resolver: created at configuration time, populated at actor startup.
        // The scheduler starts first with an empty resolver; by the time any reminder
        // actually fires, the ReminderManagerActor is registered as the shard region.
        var sharedResolver = new TestShardRegionResolver();

        return builder
            .WithLocalReminders(reminders =>
            {
                reminders.WithInMemoryStorage();
                reminders.WithResolver(_ => sharedResolver);
            })
            .StartActors((system, registry, resolver) =>
            {
                var reminderManager = system.ActorOf(
                    resolver.Props<ReminderManagerActor>(),
                    "reminder-manager");
                registry.Register<ReminderManagerActorKey>(reminderManager);

                // Register so akka-reminders delivers fired payloads to our manager
                sharedResolver.RegisterShardRegion(
                    ReminderManagerActor.ShardRegionName, reminderManager);
            });
    }

    /// <summary>
    /// Convenience method that registers all Netclaw actor infrastructure.
    /// Requires <see cref="SessionConfig"/> and <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// to be registered in DI.
    /// </summary>
    public static AkkaConfigurationBuilder WithNetclawActors(
        this AkkaConfigurationBuilder builder)
    {
        return builder
            .WithModelCapabilityCache()
            .WithSessionManager()
            .WithReminderManager();
    }
}
