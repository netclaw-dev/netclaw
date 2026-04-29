// -----------------------------------------------------------------------
// <copyright file="SourceProvenance.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Wire-safe provenance markers used to separate transport authenticity from
/// payload trust.
/// </summary>
public sealed record SourceProvenance : IWireType
{
    public TransportAuthenticity TransportAuthenticity { get; init; } = TransportAuthenticity.Unknown;

    public PayloadTaint PayloadTaint { get; init; } = PayloadTaint.Unknown;

    /// <summary>
    /// Optional scope identifier such as repository, environment, or tenant.
    /// </summary>
    public string? SourceScope { get; init; }

    /// <summary>
    /// Optional source object identifier such as a webhook event type.
    /// </summary>
    public string? SourceKind { get; init; }

    public static SourceProvenance StrictDefault() => new()
    {
        TransportAuthenticity = TransportAuthenticity.Unverified,
        PayloadTaint = PayloadTaint.Public
    };
}
