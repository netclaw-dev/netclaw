// -----------------------------------------------------------------------
// <copyright file="GenericChildPerEntityParent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Akka.Actor;
using Akka.Cluster.Sharding;

namespace Netclaw.Actors.Hosting;

/// <summary>
/// A generic "child per entity" parent actor that re-uses Akka.Cluster.Sharding's
/// <see cref="IMessageExtractor"/> for routing without requiring cluster sharding.
/// </summary>
/// <remarks>
/// Creates child actors on-demand keyed by entity ID. Same protocol works with both
/// this parent (local/test) and ShardRegion (clustered).
/// </remarks>
public sealed class GenericChildPerEntityParent : ReceiveActor
{
    public static Props CreateProps(IMessageExtractor extractor, Func<string, Props> propsFactory)
    {
        return Props.Create(() => new GenericChildPerEntityParent(extractor, propsFactory));
    }

    private readonly IMessageExtractor _extractor;
    private readonly Func<string, Props> _propsFactory;

    public GenericChildPerEntityParent(IMessageExtractor extractor, Func<string, Props> propsFactory)
    {
        _extractor = extractor;
        _propsFactory = propsFactory;

        Receive<GetActiveEntityIds>(_ =>
        {
            var entityIds = Context
                .GetChildren()
                .Select(child => Uri.UnescapeDataString(child.Path.Name))
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();

            Sender.Tell(new ActiveEntityIds(entityIds));
        });

        ReceiveAny(o =>
        {
            var entityId = _extractor.EntityId(o);
            if (entityId is null) return;
            // Entity IDs may contain characters illegal in actor names (e.g. '/').
            // Use URI-encoded form for the actor name while passing the original
            // entity ID to the props factory.
            var actorName = Uri.EscapeDataString(entityId);
            Context.Child(actorName)
                .GetOrElse(() => Context.ActorOf(_propsFactory(entityId), actorName))
                .Forward(_extractor.EntityMessage(o));
        });
    }
}
