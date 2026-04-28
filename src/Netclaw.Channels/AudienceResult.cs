// -----------------------------------------------------------------------
// <copyright file="AudienceResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Channels;

public readonly record struct AudienceResult(TrustAudience Audience, string? Error)
{
    public AudienceResult(TrustAudience audience) : this(audience, null) { }
    public AudienceResult(string error) : this(default, error) { }
}
