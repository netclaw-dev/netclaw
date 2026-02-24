using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using OllamaSharp;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Creates <see cref="IChatClient"/> instances from provider credentials
/// and model references. Looks up the named provider from the configured
/// dictionary and dispatches to the correct SDK.
/// Future provider types (OpenRouter, Anthropic, OpenAI) add cases here.
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
            "ollama" => new OllamaApiClient(
                new Uri(provider.Endpoint), model.ModelId),
            _ => throw new InvalidOperationException(
                $"Unknown provider type '{provider.Type}'. Supported: ollama")
        };
    }
}
