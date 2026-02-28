using Netclaw.Configuration;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Test double for <see cref="IProviderProbe"/> that returns canned results
/// without making real HTTP calls.
/// </summary>
public sealed class FakeProviderProbe : IProviderProbe
{
    /// <summary>
    /// Per-type results for concurrent probing scenarios.
    /// When a type is found here, it takes priority over <see cref="NextResult"/>.
    /// </summary>
    public Dictionary<string, ProviderProbeResult> TypeResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks which provider types were probed, in order.
    /// </summary>
    public List<string> ProbedTypes { get; } = [];

    /// <summary>
    /// The fallback result to return when no per-type result is configured.
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
        ProbedTypes.Add(providerType);

        var result = TypeResults.TryGetValue(providerType, out var typeResult)
            ? typeResult
            : NextResult;

        return Task.FromResult(result);
    }
}
