namespace Netclaw.Configuration;

/// <summary>
/// Well-known claim type URIs produced by Netclaw authentication schemes and
/// consumed by <c>ClaimsPrincipalMapper</c> to derive connection identity.
/// </summary>
public static class NetclawClaimTypes
{
    /// <summary>
    /// Claim carrying the <see cref="PrincipalClassification"/> enum value
    /// (string representation) for the authenticated principal.
    /// </summary>
    public const string PrincipalClassification = "netclaw:principal";

    /// <summary>
    /// Claim carrying the <see cref="TransportAuthenticity"/> enum value
    /// (string representation) for the connection's transport layer.
    /// </summary>
    public const string TransportAuthenticity = "netclaw:transport";

    /// <summary>
    /// Claim carrying the device identifier (e.g. "local" for loopback,
    /// device name for paired remote devices).
    /// </summary>
    public const string DeviceId = "netclaw:device-id";
}
