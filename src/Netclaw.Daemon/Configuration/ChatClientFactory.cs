using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using OllamaSharp;
using OpenAI;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Creates <see cref="IChatClient"/> instances from provider credentials
/// and model references. Looks up the named provider from the configured
/// dictionary and dispatches to the correct SDK.
/// </summary>
public sealed class ChatClientFactory
{
    private readonly Dictionary<string, ProviderEntry> _providers;

    public ChatClientFactory(Dictionary<string, ProviderEntry> providers)
        => _providers = providers;

    public IChatClient Create(ModelReference model)
    {
        if (!_providers.TryGetValue(model.Provider, out var provider))
            throw new InvalidOperationException(
                $"Provider '{model.Provider}' not found. "
                + $"Configured: {string.Join(", ", _providers.Keys)}");

        return provider.Type.ToLowerInvariant() switch
        {
            "ollama" => CreateOllamaClient(provider, model),
            "openai" => CreateOpenAIClient(provider, model),
            "anthropic" => CreateAnthropicClient(provider, model),
            "openrouter" => CreateOpenRouterClient(provider, model),
            _ => throw new InvalidOperationException(
                $"Unknown provider type '{provider.Type}'. "
                + $"Supported: {string.Join(", ", ProviderCapabilities.KnownProviderTypes)}")
        };
    }

    private static IChatClient CreateOllamaClient(ProviderEntry provider, ModelReference model)
    {
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? new Uri(ProviderCapabilities.GetDefaultEndpoint("ollama"))
            : new Uri(provider.Endpoint);
        return new OllamaApiClient(endpoint, model.ModelId);
    }

    private static IChatClient CreateOpenAIClient(ProviderEntry provider, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(provider, "openai");
        return new OpenAI.Chat.ChatClient(model.ModelId, apiKey)
            .AsIChatClient();
    }

    private static IChatClient CreateAnthropicClient(ProviderEntry provider, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(provider, "anthropic");
        var client = new AnthropicClient(new()
        {
            ApiKey = apiKey
        });
        return client.AsIChatClient(model.ModelId);
    }

    private static IChatClient CreateOpenRouterClient(ProviderEntry provider, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(provider, "openrouter");
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? new Uri(ProviderCapabilities.GetDefaultEndpoint("openrouter"))
            : new Uri(provider.Endpoint);

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        options.AddPolicy(new OpenRouterReasoningExcludePolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return client.GetChatClient(model.ModelId).AsIChatClient();
    }

    private static string GetRequiredApiKey(ProviderEntry provider, string providerType)
    {
        // Check API key first (works for all keyed providers)
        if (provider.ApiKey is { } apiKey && !string.IsNullOrWhiteSpace(apiKey.Value))
            return apiKey.Value;

        // Check OAuth access token as fallback for OAuth-capable providers
        if (provider.OAuthAccessToken is { } oauthToken && !string.IsNullOrWhiteSpace(oauthToken.Value))
            return oauthToken.Value;

        throw new InvalidOperationException(
            $"Provider type '{providerType}' requires authentication. "
            + "Configure ApiKey or OAuthAccessToken in secrets.json.");
    }
}
