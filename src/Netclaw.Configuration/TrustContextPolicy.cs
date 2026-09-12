// -----------------------------------------------------------------------
// <copyright file="TrustContextPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;

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
/// Canonical "all audiences" enumeration. Sourced from the enum itself so any
/// iteration that should cover every audience picks up new values automatically
/// when <see cref="TrustAudience"/> grows. Use this anywhere you would
/// otherwise hardcode <c>[Personal, Team, Public]</c> — a stale hardcoded
/// array becomes a silent privilege-escalation hazard the moment a new
/// audience is added.
/// </summary>
public static class TrustAudiences
{
    public static ImmutableArray<TrustAudience> All { get; } = [.. Enum.GetValues<TrustAudience>()];
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
/// Canonical default <see cref="TrustAudience"/> for a newly added channel or DM row, derived from
/// the deployment posture. Used by both the init wizard's channel steps and the <c>netclaw config</c>
/// channels editor so a channel added in either surface lands at the same trust tier. An unmapped
/// posture falls back to <see cref="TrustAudience.Personal"/> — the most restrictive audience — to
/// preserve the default-deny posture if <see cref="DeploymentPosture"/> ever gains a value.
/// </summary>
public static class ChannelAudienceDefaults
{
    /// <summary>Default audience for a regular channel: a Public posture publishes, otherwise Team.</summary>
    public static TrustAudience ForChannel(DeploymentPosture posture)
        => posture == DeploymentPosture.Public ? TrustAudience.Public : TrustAudience.Team;

    /// <summary>
    /// Default audience for the DM row: a single allow-listed user is always Personal; otherwise the
    /// audience tracks the posture (Public→Public, Team→Team, Personal/unmapped→Personal).
    /// </summary>
    public static TrustAudience ForDirectMessage(DeploymentPosture posture, int allowedUserCount)
        => allowedUserCount == 1
            ? TrustAudience.Personal
            : posture switch
            {
                DeploymentPosture.Public => TrustAudience.Public,
                DeploymentPosture.Team => TrustAudience.Team,
                _ => TrustAudience.Personal,
            };
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

    public static TrustBoundary ResolveBoundary(TrustBoundary? boundary, string? channelType, TrustAudience audience)
    {
        if (boundary is { } explicitBoundary)
            return explicitBoundary;

        return ResolveBoundaryFromChannelType(channelType, audience);
    }

    public static TrustBoundary ResolveBoundaryFromChannelType(string? channelType, TrustAudience audience)
    {
        if (!string.IsNullOrWhiteSpace(channelType))
        {
            switch (channelType.Trim().ToLowerInvariant())
            {
                case "slack":
                case "discord":
                case "signalr":
                case "tui":
                case "headless":
                case "console":
                case "reminder":
                case "timer":
                case "manual":
                    return TrustBoundary.TrustedInstance;
            }
        }

        return ResolveBoundaryFromAudience(audience);
    }

    public static TrustBoundary ResolveBoundaryFromSessionId(string? sessionId, TrustAudience audience = TrustAudience.Public)
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
            "slack" or "discord" or "reminder" or "timer" => TrustAudience.Team,
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

    /// <summary>
    /// Resolves the effective audience for a tool invocation, preferring the
    /// explicit parsed <paramref name="configuredAudience"/> when present and
    /// falling back to <see cref="ResolveAudienceFromSessionId"/> only when no
    /// audience was supplied at all. There is no wire-string parsing here — the
    /// audience is parsed once, upstream, when the execution context is built.
    /// </summary>
    public static TrustAudience ResolveAudienceWithFallback(TrustAudience? configuredAudience, string? sessionId)
        => configuredAudience ?? ResolveAudienceFromSessionId(sessionId);

    public static TrustBoundary ResolveBoundaryFromAudience(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => TrustBoundary.Public,
        TrustAudience.Team => TrustBoundary.Team,
        TrustAudience.Personal => TrustBoundary.Personal,
        _ => TrustBoundary.Public
    };

    /// <summary>
    /// Canonicalizes a known trust boundary string. Returns false when the
    /// boundary is blank or not one of Netclaw's supported persisted values.
    /// Accepts a raw string so legacy / on-wire input can be normalized; the
    /// canonical result is a <see cref="TrustBoundary"/>.
    /// </summary>
    public static bool TryNormalizeBoundary(string? boundary, out TrustBoundary normalizedBoundary)
    {
        normalizedBoundary = TrustBoundary.Public;
        if (string.IsNullOrWhiteSpace(boundary))
            return false;

        var trimmed = boundary.Trim();
        if (string.Equals(trimmed, TrustBoundary.Public.Value, StringComparison.OrdinalIgnoreCase))
        {
            normalizedBoundary = TrustBoundary.Public;
            return true;
        }

        if (string.Equals(trimmed, TrustBoundary.Team.Value, StringComparison.OrdinalIgnoreCase))
        {
            normalizedBoundary = TrustBoundary.Team;
            return true;
        }

        if (string.Equals(trimmed, TrustBoundary.Personal.Value, StringComparison.OrdinalIgnoreCase))
        {
            normalizedBoundary = TrustBoundary.Personal;
            return true;
        }

        // TrustedInstance is also the canonical form of the legacy
        // Slack-workspace and local-daemon boundary aliases — all three
        // share the "boundary:trusted-instance" string.
        if (string.Equals(trimmed, TrustBoundary.TrustedInstance.Value, StringComparison.OrdinalIgnoreCase))
        {
            normalizedBoundary = TrustBoundary.TrustedInstance;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when a boundary is a supported canonical value whose scope
    /// does not exceed the supplied audience. Narrower boundaries are allowed:
    /// for example, a Personal audience may persist a Public boundary.
    /// </summary>
    public static bool IsBoundaryCompatibleWithAudience(TrustBoundary boundary, TrustAudience audience)
    {
        if (!TryNormalizeBoundary(boundary.Value, out var normalizedBoundary))
            return false;

        if (normalizedBoundary == TrustBoundary.TrustedInstance)
            return audience is TrustAudience.Team or TrustAudience.Personal;

        var boundaryAudience =
            normalizedBoundary == TrustBoundary.Public ? TrustAudience.Public
            : normalizedBoundary == TrustBoundary.Team ? TrustAudience.Team
            : normalizedBoundary == TrustBoundary.Personal ? TrustAudience.Personal
            : throw new ArgumentOutOfRangeException(nameof(boundary), boundary, null);

        return boundaryAudience <= audience;
    }

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
