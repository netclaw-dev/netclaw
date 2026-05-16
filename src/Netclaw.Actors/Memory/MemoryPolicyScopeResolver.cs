// -----------------------------------------------------------------------
// <copyright file="MemoryPolicyScopeResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

internal static class MemoryPolicyScopeResolver
{
    public static TrustAudience ResolveAudience(TrustAudience? configuredAudience, string? sessionId)
        => SecurityPolicyDefaults.ResolveAudienceWithFallback(configuredAudience, sessionId);

    // Boundary is stored for future cross-trust-boundary federation but is
    // currently a single-valued constant. Audience is the sole security axis.
    public static string ResolveBoundary(string? configuredBoundary)
        => !string.IsNullOrWhiteSpace(configuredBoundary)
            ? configuredBoundary.Trim()
            : TrustBoundary.TrustedInstanceValue;
}
