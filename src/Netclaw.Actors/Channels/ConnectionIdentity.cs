// -----------------------------------------------------------------------
// <copyright file="ConnectionIdentity.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Resolved identity for an inbound SignalR connection, derived from
/// authentication claims by <see cref="ClaimsPrincipalMapper"/>.
/// </summary>
public sealed record ConnectionIdentity(
    PrincipalClassification Principal,
    TransportAuthenticity Transport,
    SenderId SenderId);
