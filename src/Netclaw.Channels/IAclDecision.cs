// -----------------------------------------------------------------------
// <copyright file="IAclDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels;

public interface IAclDecision
{
    bool IsAllowed { get; }
    string? DenyReason { get; }
    TrustAudience Audience { get; }
    PrincipalClassification Principal { get; }
    SourceProvenance Provenance { get; }
}
