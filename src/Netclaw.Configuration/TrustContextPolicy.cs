namespace Netclaw.Configuration;

/// <summary>
/// Ordered exposure levels used across channels, memories, tools, and outputs.
/// Broader audiences are less trusted and should win during strict-default fallback.
/// </summary>
public enum TrustAudience
{
    Public,
    Team,
    Personal
}

/// <summary>
/// Deployment-level trust posture ceiling.
/// </summary>
public enum DeploymentPosture
{
    Public,
    Team,
    Personal
}

/// <summary>
/// Classification of the principal currently contacting the bot.
/// </summary>
public enum PrincipalClassification
{
    UntrustedExternal,
    TrustedInternal,
    Operator,
    VerifiedAutomation,
    SystemProcess
}

/// <summary>
/// How confidently the transport/source has been authenticated.
/// </summary>
public enum TransportAuthenticity
{
    Unknown,
    Unverified,
    Verified,
    LocalProcess
}

/// <summary>
/// Risk classification for content provenance handled during a turn.
/// </summary>
public enum PayloadTaint
{
    Unknown,
    Trusted,
    Community,
    Public,
    SensitiveRead
}

/// <summary>
/// How shell execution is permitted to run for a deployment.
/// </summary>
public enum ShellExecutionMode
{
    Off,
    SandboxOnly,
    HostAllowed
}

/// <summary>
/// Configuration entry point for trust-context defaults.
/// Nullable values let runtime strict-default resolution distinguish between
/// explicit operator intent and missing policy.
/// </summary>
public sealed class SecurityPolicyConfig
{
    public DeploymentPosture? DeploymentPosture { get; set; }

    public ShellExecutionMode? ShellExecutionMode { get; set; }

    public bool StrictDefaults { get; set; } = true;
}

/// <summary>
/// Resolved deployment defaults after applying strict fallback behavior.
/// </summary>
public sealed record EffectivePolicyDefaults(
    DeploymentPosture DeploymentPosture,
    TrustAudience Audience,
    ShellExecutionMode ShellExecutionMode,
    bool UsedStrictFallback);

public static class SecurityPolicyDefaults
{
    public const string PublicBoundary = "boundary:public";
    public const string TeamBoundary = "boundary:team";
    public const string PersonalBoundary = "boundary:personal";
    public const string TrustedInstanceBoundary = "boundary:trusted-instance";
    public const string SlackWorkspaceBoundary = TrustedInstanceBoundary;
    public const string LocalDaemonBoundary = TrustedInstanceBoundary;
    public const string LegacyRestrictedBoundary = "boundary:legacy-restricted";
    public const string DefaultMemoryDomain = "project:default";

    public static string ToWireValue(this TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "public",
        TrustAudience.Team => "team",
        TrustAudience.Personal => "personal",
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, null)
    };

    public static bool TryParseAudience(string? wire, out TrustAudience audience)
    {
        if (string.Equals(wire, "public", StringComparison.OrdinalIgnoreCase))
        {
            audience = TrustAudience.Public;
            return true;
        }

        if (string.Equals(wire, "team", StringComparison.OrdinalIgnoreCase))
        {
            audience = TrustAudience.Team;
            return true;
        }

        if (string.Equals(wire, "personal", StringComparison.OrdinalIgnoreCase))
        {
            audience = TrustAudience.Personal;
            return true;
        }

        audience = default;
        return false;
    }

    public static string ResolveBoundary(string? boundary, string? channelType, TrustAudience audience)
    {
        if (!string.IsNullOrWhiteSpace(boundary))
            return boundary.Trim();

        return ResolveBoundaryFromChannelType(channelType, audience);
    }

    public static string ResolveBoundaryFromChannelType(string? channelType, TrustAudience audience)
    {
        if (!string.IsNullOrWhiteSpace(channelType))
        {
            switch (channelType.Trim().ToLowerInvariant())
            {
                case "slack":
                    return TrustedInstanceBoundary;
                case "signalr":
                case "tui":
                case "headless":
                case "console":
                case "reminder":
                case "timer":
                case "manual":
                    return TrustedInstanceBoundary;
            }
        }

        return ResolveBoundaryFromAudience(audience);
    }

    public static string ResolveBoundaryFromSessionId(string? sessionId, TrustAudience audience = TrustAudience.Public)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ResolveBoundaryFromAudience(audience);

        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        var prefix = slash > 0 ? sessionId[..slash] : sessionId;
        return ResolveBoundaryFromChannelType(prefix, audience);
    }

    public static TrustAudience ResolveAudienceFromChannelType(string? channelType)
    {
        if (string.IsNullOrWhiteSpace(channelType))
            return TrustAudience.Public;

        return channelType.Trim().ToLowerInvariant() switch
        {
            "signalr" or "tui" or "headless" or "console" or "manual" => TrustAudience.Personal,
            "slack" or "reminder" or "timer" => TrustAudience.Team,
            _ => TrustAudience.Public
        };
    }

    public static TrustAudience ResolveAudienceFromSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return TrustAudience.Public;

        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        var prefix = slash > 0 ? sessionId[..slash] : sessionId;
        return ResolveAudienceFromChannelType(prefix);
    }

    public static string InferLegacyBoundaryFromDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return LegacyRestrictedBoundary;

        var normalized = domain.Trim().ToLowerInvariant();
        if (normalized is "project:signalr" or "project:tui" or "project:headless" or "project:manual" or DefaultMemoryDomain)
            return TrustedInstanceBoundary;

        if (normalized.StartsWith("user:", StringComparison.Ordinal) || normalized.StartsWith("person:", StringComparison.Ordinal))
            return TrustedInstanceBoundary;

        if (normalized.StartsWith("project:", StringComparison.Ordinal))
            return TrustedInstanceBoundary;

        return LegacyRestrictedBoundary;
    }

    public static string ResolveBoundaryFromAudience(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => PublicBoundary,
        TrustAudience.Team => TeamBoundary,
        TrustAudience.Personal => PersonalBoundary,
        _ => PublicBoundary
    };

    public static EffectivePolicyDefaults Resolve(SecurityPolicyConfig? config)
    {
        var strictDefaults = config?.StrictDefaults ?? true;
        var posture = ResolveDeploymentPosture(config?.DeploymentPosture, strictDefaults);
        var shellMode = ResolveShellExecutionMode(posture, config?.ShellExecutionMode, strictDefaults);
        return new EffectivePolicyDefaults(
            posture,
            ResolveAudience(posture),
            shellMode,
            UsedStrictFallback: config is null || strictDefaults);
    }

    public static DeploymentPosture ResolveDeploymentPosture(DeploymentPosture? configured, bool strictDefaults = true)
    {
        if (configured.HasValue)
            return configured.Value;

        return strictDefaults ? DeploymentPosture.Public : DeploymentPosture.Personal;
    }

    public static TrustAudience ResolveAudience(DeploymentPosture posture) => posture switch
    {
        DeploymentPosture.Public => TrustAudience.Public,
        DeploymentPosture.Team => TrustAudience.Team,
        _ => TrustAudience.Personal
    };

    public static ShellExecutionMode ResolveShellExecutionMode(
        DeploymentPosture posture,
        ShellExecutionMode? configured,
        bool strictDefaults = true)
    {
        if (configured.HasValue)
            return configured.Value;

        if (!strictDefaults)
            return posture == DeploymentPosture.Personal ? ShellExecutionMode.HostAllowed : ShellExecutionMode.Off;

        return posture switch
        {
            DeploymentPosture.Personal => ShellExecutionMode.HostAllowed,
            _ => ShellExecutionMode.Off
        };
    }
}
