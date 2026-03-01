using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Daemon.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Creates <see cref="IChatClient"/> instances using the plugin registry.
/// Replaces <c>ChatClientFactory</c> by delegating to the appropriate
/// <see cref="ILlmProviderPlugin.CreateChatClient"/>.
/// </summary>
public sealed class ProviderPluginFactory
{
    private readonly Dictionary<string, ProviderEntry> _providers;
    private readonly Dictionary<string, ILlmProviderPlugin> _plugins;

    public ProviderPluginFactory(
        Dictionary<string, ProviderEntry> providers,
        IEnumerable<ILlmProviderPlugin> plugins)
    {
        _providers = providers;
        _plugins = new Dictionary<string, ILlmProviderPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
            _plugins[plugin.TypeKey] = plugin;
    }

    public IChatClient Create(ModelReference model)
    {
        if (!_providers.TryGetValue(model.Provider, out var provider))
            throw new InvalidOperationException(
                $"Provider '{model.Provider}' not found. "
                + $"Configured: {string.Join(", ", _providers.Keys)}");

        if (!_plugins.TryGetValue(provider.Type, out var plugin))
            throw new InvalidOperationException(
                $"Unknown provider type '{provider.Type}'. "
                + $"Supported: {string.Join(", ", _plugins.Keys)}");

        return plugin.CreateChatClient(provider, model);
    }
}
