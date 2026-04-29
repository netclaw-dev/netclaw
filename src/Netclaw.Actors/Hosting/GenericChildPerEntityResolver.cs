// -----------------------------------------------------------------------
// <copyright file="GenericChildPerEntityResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;

namespace Netclaw.Actors.Hosting;

/// <summary>
/// Routes reminders to <see cref="GenericChildPerEntityParent"/> actors via ActorRegistry,
/// bypassing Akka.Cluster.Sharding. Messages are delivered directly without ShardingEnvelope
/// wrapping since the parent routes based on <see cref="Protocol.IWithSessionId"/>.
/// </summary>
public sealed class GenericChildPerEntityResolver : IShardRegionResolver
{
    public const string SessionManagerRegion = "session-manager";

    private readonly Func<IActorRegistry> _registryFactory;
    private IActorRegistry? _registry;

    public GenericChildPerEntityResolver(ActorSystem system)
        : this(() => ActorRegistry.For(system)) { }

    public GenericChildPerEntityResolver(Func<IActorRegistry> registryFactory)
    {
        _registryFactory = registryFactory;
    }

    private IActorRegistry Registry => _registry ??= _registryFactory();

    public IActorRef? TryResolve(ReminderEntity entity) => entity.ShardRegionName switch
    {
        SessionManagerRegion => Registry.TryGet<SessionManagerActorKey>(out var mgr) ? mgr : null,
        _ => null
    };

    public void DeliverReminder(ReminderEntity entity, ReminderEnvelope envelope, IActorRef? sender = null)
    {
        TryResolve(entity)?.Tell(envelope, sender ?? ActorRefs.NoSender);
    }
}
