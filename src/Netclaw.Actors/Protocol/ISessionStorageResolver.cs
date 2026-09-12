// -----------------------------------------------------------------------
// <copyright file="ISessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Resolves one durable storage layout before a consumer writes session data.
/// </summary>
public interface ISessionStorageResolver
{
    /// <summary>Gets or atomically creates the immutable storage binding for <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">The stable session identity.</param>
    /// <returns>The complete resolved storage layout.</returns>
    SessionStoragePaths Resolve(SessionId sessionId);
}
