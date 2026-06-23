// -----------------------------------------------------------------------
// <copyright file="IProviderProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Result of probing a provider's model listing API.
/// Validates credentials and discovers available models in one call.
/// </summary>
public sealed record ProviderProbeResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<DiscoveredModel> Models);

/// <summary>
/// Probes a provider's model listing API to validate credentials
/// and discover available models.
/// </summary>
public interface IProviderProbe
{
    /// <summary>
    /// Probe using individual parameters. Cannot distinguish OAuth tokens from API keys.
    /// Prefer <see cref="ProbeAsync(ProviderEntry, CancellationToken)"/> which preserves
    /// the OAuth token distinction needed for provider-specific behavior.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Probe using a full <see cref="ProviderEntry"/> which preserves the distinction
    /// between API keys and OAuth tokens. This is the preferred overload.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Probe with explicit auth method. When the auth method is OAuth, the credential
    /// is set as <see cref="ProviderEntry.OAuthAccessToken"/> instead of <see cref="ProviderEntry.ApiKey"/>.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? credential,
        AuthMethod authMethod, CancellationToken ct = default);
}

/// <summary>
/// Probes a provider entry that is already persisted under a known config key.
/// Implementations may use <paramref name="providerName"/> to update persisted
/// runtime metadata, such as refreshed OAuth credentials.
/// </summary>
/// <remarks>
/// Use this only for configured providers. Pending add/fix flows should keep
/// using <see cref="IProviderProbe"/> so failed validation cannot overwrite
/// existing credentials.
/// </remarks>
public interface IConfiguredProviderProbe
{
    Task<ProviderProbeResult> ProbeConfiguredAsync(
        string providerName,
        ProviderEntry entry,
        CancellationToken ct = default);
}
