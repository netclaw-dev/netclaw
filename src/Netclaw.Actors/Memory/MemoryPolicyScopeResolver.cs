using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

internal static class MemoryPolicyScopeResolver
{
    public static TrustAudience ResolveAudience(string? configuredAudience, string? sessionId)
        => SecurityPolicyDefaults.TryParseAudience(configuredAudience, out var parsed)
            ? parsed
            : SecurityPolicyDefaults.ResolveAudienceFromSessionId(sessionId);

    public static string ResolveBoundary(string? configuredBoundary, TrustAudience audience, string? sessionId, string? domain = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredBoundary))
            return configuredBoundary.Trim();

        if (!string.IsNullOrWhiteSpace(sessionId))
            return SecurityPolicyDefaults.ResolveBoundaryFromSessionId(sessionId, audience);

        if (!string.IsNullOrWhiteSpace(domain))
            return SecurityPolicyDefaults.InferLegacyBoundaryFromDomain(domain);

        return SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience);
    }
}
