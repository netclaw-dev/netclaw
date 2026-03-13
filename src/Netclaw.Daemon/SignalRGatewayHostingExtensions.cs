using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Daemon.Gateway;

namespace Netclaw.Daemon;

/// <summary>
/// Marker type for <see cref="Akka.Hosting.IActorRegistry"/> lookup of the SignalR
/// gateway parent actor (GenericChildPerEntityParent routing to SignalRSessionActors).
/// </summary>
public sealed class SignalRGatewayActorKey;

/// <summary>
/// Akka.Hosting extension that registers the SignalR session gateway actor.
/// </summary>
public static class SignalRGatewayHostingExtensions
{
    /// <summary>
    /// Registers the <c>signalr-gateway</c> actor, a
    /// <see cref="GenericChildPerEntityParent"/> that creates and routes messages to
    /// per-session <see cref="SignalRSessionActor"/> children.
    /// Stream actors created by each <see cref="SignalRSessionActor"/> are scoped to
    /// that actor's materializer and are automatically stopped on actor passivation,
    /// eliminating the StreamSupervisor actor leak caused by system-level materialization.
    /// </summary>
    public static AkkaConfigurationBuilder WithSignalRGateway(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var gateway = system.ActorOf(
                GenericChildPerEntityParent.CreateProps(
                    new SignalRMessageExtractor(),
                    entityId => resolver.Props<SignalRSessionActor>(entityId)),
                "signalr-gateway");

            registry.Register<SignalRGatewayActorKey>(gateway);
        });
    }
}
