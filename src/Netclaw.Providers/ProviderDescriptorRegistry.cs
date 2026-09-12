// -----------------------------------------------------------------------
// <copyright file="ProviderDescriptorRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers;

/// <summary>
/// Aggregates all registered <see cref="IProviderDescriptor"/> instances
/// and provides lookup by type key. Also implements <see cref="IProviderProbe"/>
/// for backward compatibility with code that expects the old probe interface.
/// </summary>
public sealed class ProviderDescriptorRegistry : IProviderProbe
{
    private readonly Dictionary<string, IProviderDescriptor> _descriptors;
    private readonly IReadOnlyList<string> _knownTypeKeys;

    public ProviderDescriptorRegistry(IEnumerable<IProviderDescriptor> descriptors)
    {
        _descriptors = new Dictionary<string, IProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in descriptors)
            _descriptors[d.TypeKey] = d;
        _knownTypeKeys = _descriptors.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// All known provider type keys, in alphabetical order.
    /// Replaces <c>ProviderCapabilities.KnownProviderTypes</c>.
    /// </summary>
    public IReadOnlyList<string> KnownTypeKeys => _knownTypeKeys;

    /// <summary>
    /// Get a descriptor by type key. Throws if not found.
    /// </summary>
    public IProviderDescriptor Get(string typeKey)
    {
        if (_descriptors.TryGetValue(typeKey, out var d))
            return d;
        throw new ArgumentException(
            $"Unknown provider type '{typeKey}'. Known: {string.Join(", ", KnownTypeKeys)}",
            nameof(typeKey));
    }

    /// <summary>
    /// Try to get a descriptor by type key.
    /// </summary>
    public bool TryGet(string typeKey, out IProviderDescriptor descriptor)
        => _descriptors.TryGetValue(typeKey, out descriptor!);

    /// <inheritdoc />
    public Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? apiKey,
        CancellationToken ct = default)
    {
        if (!TryGet(providerType, out var descriptor))
            return Task.FromResult(new ProviderProbeResult(false,
                $"Unknown provider type: {providerType}", []));

        // Build a temporary ProviderEntry from the probe parameters
        var entry = new ProviderEntry
        {
            Type = providerType,
            Endpoint = endpoint ?? "",
            ApiKey = apiKey is not null ? new SensitiveString(apiKey) : null,
        };

        return descriptor.ProbeAsync(entry, ct);
    }

    /// <summary>
    /// Probe using a full ProviderEntry, preserving OAuth token distinction.
    /// </summary>
    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        if (!TryGet(entry.Type, out var descriptor))
            return Task.FromResult(new ProviderProbeResult(false,
                $"Unknown provider type: {entry.Type}", []));

        return descriptor.ProbeAsync(entry, ct);
    }

    /// <summary>
    /// Probe with explicit auth method. When the auth method is OAuth, the credential
    /// is set as <see cref="ProviderEntry.OAuthAccessToken"/> instead of <see cref="ProviderEntry.ApiKey"/>.
    /// This allows descriptors to distinguish OAuth tokens from API keys.
    /// </summary>
    public Task<ProviderProbeResult> ProbeAsync(
        string providerType, string? endpoint, string? credential,
        AuthMethod authMethod, CancellationToken ct = default)
    {
        if (!TryGet(providerType, out var descriptor))
            return Task.FromResult(new ProviderProbeResult(false,
                $"Unknown provider type: {providerType}", []));

        var sensitive = credential is not null ? new SensitiveString(credential) : null;
        var entry = new ProviderEntry
        {
            Type = providerType,
            Endpoint = endpoint ?? "",
            AuthMethod = authMethod,
            ApiKey = authMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice ? null : sensitive,
            OAuthAccessToken = authMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice ? sensitive : null,
        };

        return descriptor.ProbeAsync(entry, ct);
    }
}
