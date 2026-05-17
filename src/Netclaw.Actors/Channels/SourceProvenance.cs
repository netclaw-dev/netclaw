// -----------------------------------------------------------------------
// <copyright file="SourceProvenance.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Wire-safe provenance markers used to separate transport authenticity from
/// payload trust. Both trust-bearing fields are positional and mandatory —
/// there is no permissive sentinel default a forgetful caller can inherit.
/// </summary>
public sealed record SourceProvenance(
    TransportAuthenticity TransportAuthenticity,
    PayloadTaint PayloadTaint) : IWireType
{
    /// <summary>
    /// Optional scope identifier such as repository, environment, or tenant.
    /// </summary>
    public SourceScope? SourceScope { get; init; }

    /// <summary>
    /// Optional source object identifier such as a webhook event type.
    /// </summary>
    public SourceKind? SourceKind { get; init; }
}
