namespace Netclaw.Configuration.Providers;

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

    /// <summary>
    /// <see cref="IProviderProbe"/> implementation that delegates to the
    /// appropriate descriptor's <see cref="IProviderDescriptor.ProbeAsync"/>.
    /// </summary>
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
}
