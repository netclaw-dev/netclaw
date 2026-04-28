// -----------------------------------------------------------------------
// <copyright file="IWithSessionId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Marker interface for messages routable to session actors.
/// Used by <see cref="Routing.SessionMessageExtractor"/> to extract entity IDs.
/// </summary>
public interface IWithSessionId
{
    SessionId SessionId { get; }
}
