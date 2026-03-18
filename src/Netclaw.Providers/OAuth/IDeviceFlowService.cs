using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Abstraction for device authorization flows.
/// Implementations may use standard RFC 8628 or provider-specific protocols.
/// </summary>
public interface IDeviceFlowService
{
    /// <summary>
    /// Initiate device authorization — requests a user code and verification URI.
    /// </summary>
    Task<DeviceAuthorizationResponse> StartDeviceAuthorizationAsync(
        OAuthDeviceFlowConfig config, CancellationToken ct = default);

    /// <summary>
    /// Poll until the user authorizes, denies, or the code expires.
    /// </summary>
    Task<OAuthDeviceFlowResult> PollForTokenAsync(
        OAuthDeviceFlowConfig config,
        DeviceAuthorizationResponse deviceAuth,
        Action<DeviceFlowState>? onStateChanged = null,
        CancellationToken ct = default);

    /// <summary>
    /// Exchange a refresh token for a new access token.
    /// Returns null if the refresh token is invalid or revoked.
    /// </summary>
    Task<OAuthDeviceFlowResult?> RefreshTokenAsync(
        string tokenEndpoint,
        string clientId,
        SensitiveString refreshToken,
        CancellationToken ct = default);

    /// <summary>
    /// Backward-compatible overload that accepts a raw refresh token string.
    /// </summary>
    Task<OAuthDeviceFlowResult?> RefreshTokenAsync(
        string tokenEndpoint,
        string clientId,
        string refreshToken,
        CancellationToken ct = default) =>
        RefreshTokenAsync(tokenEndpoint, clientId, new SensitiveString(refreshToken), ct);
}
