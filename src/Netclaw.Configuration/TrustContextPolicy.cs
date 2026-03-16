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
