// -----------------------------------------------------------------------
// <copyright file="SessionMessageExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Cluster.Sharding;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Routing;

/// <summary>
/// Extracts session entity IDs from messages implementing <see cref="IWithSessionId"/>.
/// Used by both <see cref="Hosting.GenericChildPerEntityParent"/> (local) and
/// Akka.Cluster.Sharding (clustered) to route messages to session actors.
/// </summary>
public sealed class SessionMessageExtractor : HashCodeMessageExtractor
{
    public const int DefaultShardCount = 40;

    public SessionMessageExtractor(int maxNumberOfShards = DefaultShardCount)
        : base(maxNumberOfShards) { }

    public override string? EntityId(object message) => message switch
    {
        IWithSessionId msg => msg.SessionId.Value,
        _ => null
    };
}
