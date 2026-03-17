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
    Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Probe using a full <see cref="ProviderEntry"/> which preserves the distinction
    /// between API keys and OAuth tokens. Prefer this overload when the entry is
    /// already loaded from config.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default);
}
