// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Configuration;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Query the <see cref="ModelCapabilityActor"/> for a model's capabilities.
/// </summary>
public sealed record GetModelCapabilities(ModelId ModelId) : INoSerializationVerificationNeeded;

/// <summary>
/// Response from the capability cache containing resolved modalities.
/// </summary>
public sealed record ModelCapabilitiesResponse(
    ModelId ModelId,
    ModelModality InputModalities,
    ModelModality OutputModalities) : INoSerializationVerificationNeeded;

/// <summary>
/// Internal message piped back from async resolution to the actor.
/// </summary>
internal sealed record CapabilityResolved(
    ModelId ModelId,
    ModelModality InputModalities,
    ModelModality OutputModalities,
    bool Success) : INoSerializationVerificationNeeded;
