using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.Descriptors;
using OpenAI;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Daemon-side plugin for OpenRouter. Wraps <see cref="OpenRouterDescriptor"/>
/// and adds SDK client construction with reasoning-exclude pipeline policy.
/// </summary>
public sealed class OpenRouterProviderPlugin : ILlmProviderPlugin
{
    private readonly OpenRouterDescriptor _descriptor;

    public OpenRouterProviderPlugin(OpenRouterDescriptor descriptor)
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
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        options.AddPolicy(new OpenRouterReasoningExcludePolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return client.GetChatClient(model.ModelId).AsIChatClient();
    }

    public IVendorOptionsSource? CreateVendorOptionsSource(ProviderEntry entry)
        => new OpenRouterVendorOptionsSource();

    private static string GetRequiredApiKey(ProviderEntry provider)
    {
        if (provider.ApiKey is { } apiKey && !string.IsNullOrWhiteSpace(apiKey.Value))
            return apiKey.Value;
        if (provider.OAuthAccessToken is { } oauthToken && !string.IsNullOrWhiteSpace(oauthToken.Value))
            return oauthToken.Value;
        throw new InvalidOperationException(
            "Provider type 'openrouter' requires authentication. "
            + "Configure ApiKey or OAuthAccessToken in secrets.json.");
    }
}
