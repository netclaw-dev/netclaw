// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Query the <see cref="ModelCapabilityActor"/> for a model's capabilities.
/// </summary>
public sealed record GetModelCapabilities(string ModelId);

/// <summary>
/// Response from the capability cache containing resolved modalities.
/// </summary>
public sealed record ModelCapabilitiesResponse(
    string ModelId,
    ModelModality InputModalities,
    ModelModality OutputModalities);

/// <summary>
/// Internal message piped back from async resolution to the actor.
/// </summary>
internal sealed record CapabilityResolved(
    string ModelId,
    ModelModality InputModalities,
    ModelModality OutputModalities,
    bool Success);
