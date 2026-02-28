using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;
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
    /// Convenience method that registers all Netclaw actor infrastructure.
    /// Requires <see cref="SessionConfig"/> and <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// to be registered in DI.
    /// </summary>
    public static AkkaConfigurationBuilder WithNetclawActors(
        this AkkaConfigurationBuilder builder)
    {
        return builder
            .WithModelCapabilityCache()
            .WithSessionManager();
    }
}
