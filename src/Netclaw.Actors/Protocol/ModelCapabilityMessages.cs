// -----------------------------------------------------------------------
// <copyright file="ModelCapabilityMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Configuration;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// External message contract for <see cref="ModelCapabilityActor"/>.
/// </summary>
public static class ModelCapabilityProtocol
{
    /// <summary>Marker for model-capability queries.</summary>
    public interface IModelCapabilityQuery : INoSerializationVerificationNeeded;

    /// <summary>Marker for model-capability responses.</summary>
    public interface IModelCapabilityResponse : INoSerializationVerificationNeeded;

    // ===== Queries =====

    /// <summary>
    /// Query the <see cref="ModelCapabilityActor"/> for a model's capabilities.
    /// </summary>
    public sealed record GetModelCapabilities(ModelId ModelId) : IModelCapabilityQuery;

    // ===== Responses =====

    /// <summary>
    /// Response from the capability cache containing resolved modalities.
    /// </summary>
    public sealed record ModelCapabilitiesResponse(
        ModelId ModelId,
        ModelModality InputModalities,
        ModelModality OutputModalities) : IModelCapabilityResponse;
}

/// <summary>
/// Internal message piped back from async resolution to the actor.
/// </summary>
internal sealed record CapabilityResolved(
    ModelId ModelId,
    ModelModality InputModalities,
    ModelModality OutputModalities,
    bool Success) : INoSerializationVerificationNeeded;
