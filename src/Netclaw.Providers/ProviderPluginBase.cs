using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers;

/// <summary>
/// Base class for provider plugins that eliminates IProviderDescriptor
/// delegation boilerplate. Each plugin only needs to implement
/// <see cref="CreateChatClient"/>.
/// </summary>
public abstract class ProviderPluginBase<TDescriptor> : ILlmProviderPlugin
    where TDescriptor : IProviderDescriptor
{
    protected TDescriptor Descriptor { get; }

    protected ProviderPluginBase(TDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public string TypeKey => Descriptor.TypeKey;
    public string DisplayName => Descriptor.DisplayName;
    public string DefaultEndpoint => Descriptor.DefaultEndpoint;
    public string ModelListingPath => Descriptor.ModelListingPath;
    public IProviderAuth Auth => Descriptor.Auth;

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        => Descriptor.ProbeAsync(entry, ct);

    public abstract IChatClient CreateChatClient(ProviderEntry entry, ModelReference model);

    public virtual IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry) => null;

    /// <summary>
    /// Resolves the API key or OAuth token from a provider entry.
    /// Throws if neither is available.
    /// </summary>
    protected static string GetRequiredApiKey(ProviderEntry provider, string providerType)
    {
        if (provider.ApiKey is { } apiKey && !string.IsNullOrWhiteSpace(apiKey.Value))
            return apiKey.Value;
        if (provider.OAuthAccessToken is { } oauthToken && !string.IsNullOrWhiteSpace(oauthToken.Value))
            return oauthToken.Value;
        throw new InvalidOperationException(
            $"Provider type '{providerType}' requires authentication. "
            + "Configure ApiKey or OAuthAccessToken in secrets.json.");
    }
}
