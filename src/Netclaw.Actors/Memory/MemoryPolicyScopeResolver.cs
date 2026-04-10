using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

internal static class MemoryPolicyScopeResolver
{
    public static TrustAudience ResolveAudience(string? configuredAudience, string? sessionId)
        => SecurityPolicyDefaults.TryParseAudience(configuredAudience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(sessionId);

    public static string ResolveBoundary(string? configuredBoundary, TrustAudience audience, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(configuredBoundary))
            return configuredBoundary.Trim();

        // All Netclaw memory runs inside a trusted local daemon instance.
        // Session ID / audience used to be used to derive a boundary, but
        // audience is now the only security axis — boundary is effectively
        // a single-valued constant. Kept as a stored field for potential
        // future cross-trust-boundary federation.
        return SecurityPolicyDefaults.TrustedInstanceBoundary;
    }
}
