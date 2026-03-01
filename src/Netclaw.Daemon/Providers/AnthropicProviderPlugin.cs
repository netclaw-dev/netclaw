using Anthropic;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.Descriptors;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for Anthropic. Wraps <see cref="AnthropicDescriptor"/>
/// and adds SDK client construction.
/// </summary>
public sealed class AnthropicProviderPlugin : ILlmProviderPlugin
{
    private readonly AnthropicDescriptor _descriptor;

    public AnthropicProviderPlugin(AnthropicDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string TypeKey => _descriptor.TypeKey;
    public string DisplayName => _descriptor.DisplayName;
    public IReadOnlyList<AuthMethod> SupportedAuthMethods => _descriptor.SupportedAuthMethods;
    public string DefaultEndpoint => _descriptor.DefaultEndpoint;
    public string ModelListingPath => _descriptor.ModelListingPath;
    public CredentialInputMode CredentialMode => _descriptor.CredentialMode;
    public string? ApiKeyGuidanceUrl => _descriptor.ApiKeyGuidanceUrl;

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        => _descriptor.ProbeAsync(entry, ct);

    public IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry);
        var client = new AnthropicClient(new()
        {
            ApiKey = apiKey
        });
        return client.AsIChatClient(model.ModelId);
    }

    private static string GetRequiredApiKey(ProviderEntry provider)
    {
        if (provider.ApiKey is { } apiKey && !string.IsNullOrWhiteSpace(apiKey.Value))
            return apiKey.Value;
        if (provider.OAuthAccessToken is { } oauthToken && !string.IsNullOrWhiteSpace(oauthToken.Value))
            return oauthToken.Value;
        throw new InvalidOperationException(
            "Provider type 'anthropic' requires authentication. "
            + "Configure ApiKey or OAuthAccessToken in secrets.json.");
    }
}
