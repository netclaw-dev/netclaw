using Netclaw.Cli.Tui;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Test double for <see cref="IProviderProbe"/> that returns canned results
/// without making real HTTP calls.
/// </summary>
public sealed class FakeProviderProbe : IProviderProbe
{
    /// <summary>
    /// The result to return from the next <see cref="ProbeAsync"/> call.
    /// Defaults to a successful probe with two sample models.
    /// </summary>
    public ProviderProbeResult NextResult { get; set; } = new(
        true, null,
        [
            new DiscoveredModel { ModelId = "model-a" },
            new DiscoveredModel { ModelId = "model-b" }
        ]);

    /// <summary>
    /// Number of times <see cref="ProbeAsync"/> has been called.
    /// </summary>
    public int ProbeCallCount { get; private set; }

    /// <summary>
    /// The provider type from the last call.
    /// </summary>
    public string? LastProviderType { get; private set; }

    public Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastProviderType = providerType;
        return Task.FromResult(NextResult);
    }
}
