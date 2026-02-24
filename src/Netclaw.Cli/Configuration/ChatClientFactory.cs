using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using OllamaSharp;

namespace Netclaw.Cli.Configuration;

/// <summary>
/// Transitional: duplicated from Netclaw.Daemon. Removed in Task 1.28
/// when CLI connects to daemon via SignalR instead of running in-process.
/// </summary>
internal sealed class ChatClientFactory
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
