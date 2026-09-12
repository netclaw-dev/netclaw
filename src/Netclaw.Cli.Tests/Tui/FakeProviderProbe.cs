// -----------------------------------------------------------------------
// <copyright file="FakeProviderProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Test double for <see cref="IProviderProbe"/> that returns canned results
/// without making real HTTP calls.
/// </summary>
public sealed class FakeProviderProbe : IProviderProbe, IConfiguredProviderProbe
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
    /// Tracks provider names passed through the configured-provider probe path.
    /// </summary>
    public List<string> ConfiguredProviderNames { get; } = [];

    /// <summary>
    /// The fallback result to return when no per-type result is configured.
    /// Defaults to a successful probe with two sample models.
    /// </summary>
    public ProviderProbeResult NextResult { get; set; } = new(
        true, null,
        [
            new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("model-a") },
            new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("model-b") }
        ]);

    /// <summary>
    /// Optional exception to throw from <see cref="ProbeAsync"/>.
    /// Useful for testing probe error handling paths.
    /// </summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// Number of times <see cref="ProbeAsync"/> has been called.
    /// </summary>
    public int ProbeCallCount { get; private set; }

    /// <summary>
    /// The provider type from the last call.
    /// </summary>
    public string? LastProviderType { get; private set; }

    /// <summary>
    /// The credential value passed in the last call.
    /// </summary>
    public string? LastApiKey { get; private set; }

    /// <summary>
    /// Optional gate. When set, <see cref="ProbeAsync(string, string?, string?, CancellationToken)"/>
    /// blocks (observing the cancellation token) until the gate is completed — used to stage
    /// in-flight probes for cancellation/concurrency tests. Null (default) returns immediately.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastProviderType = providerType;
        LastApiKey = apiKey;
        ProbedTypes.Add(providerType);

        if (Gate is not null)
            await Gate.Task.WaitAsync(ct);

        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return TypeResults.TryGetValue(providerType, out var typeResult)
            ? typeResult
            : NextResult;
    }

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        return ProbeAsync(entry.Type, entry.Endpoint, entry.ApiKey?.Value ?? entry.OAuthAccessToken?.Value, ct);
    }

    public Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? credential,
        AuthMethod authMethod, CancellationToken ct = default)
    {
        return ProbeAsync(providerType, endpoint, credential, ct);
    }

    public Task<ProviderProbeResult> ProbeConfiguredAsync(
        string providerName,
        ProviderEntry entry,
        CancellationToken ct = default)
    {
        ConfiguredProviderNames.Add(providerName);
        return ProbeAsync(entry, ct);
    }
}
