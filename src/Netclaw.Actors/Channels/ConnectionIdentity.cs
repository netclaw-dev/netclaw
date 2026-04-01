using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Resolved identity for an inbound SignalR connection, derived from
/// authentication claims by <see cref="ClaimsPrincipalMapper"/>.
/// </summary>
public sealed record ConnectionIdentity(
    PrincipalClassification Principal,
    TransportAuthenticity Transport,
    string SenderId);
