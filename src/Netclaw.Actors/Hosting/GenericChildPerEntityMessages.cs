// -----------------------------------------------------------------------
// <copyright file="GenericChildPerEntityMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Hosting;

/// <summary>
/// Queries the currently live entity IDs hosted by a <see cref="GenericChildPerEntityParent"/>.
/// </summary>
public sealed record GetActiveEntityIds
{
    public static readonly GetActiveEntityIds Instance = new();

    private GetActiveEntityIds()
    {
    }
}

/// <summary>
/// Response containing the currently live entity IDs for a <see cref="GenericChildPerEntityParent"/>.
/// </summary>
public sealed record ActiveEntityIds(IReadOnlyList<string> EntityIds);
