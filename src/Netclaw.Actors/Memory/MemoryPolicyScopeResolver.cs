using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

internal static class MemoryPolicyScopeResolver
{
    public static TrustAudience ResolveAudience(string? configuredAudience, string? sessionId)
        => SecurityPolicyDefaults.TryParseAudience(configuredAudience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(sessionId);

    // Boundary is stored for future cross-trust-boundary federation but is
    // currently a single-valued constant. Audience is the sole security axis.
    public static string ResolveBoundary(string? configuredBoundary)
        => !string.IsNullOrWhiteSpace(configuredBoundary)
            ? configuredBoundary.Trim()
            : SecurityPolicyDefaults.TrustedInstanceBoundary;
}
