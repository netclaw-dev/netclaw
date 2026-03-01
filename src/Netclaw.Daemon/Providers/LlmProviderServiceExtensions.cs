using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Providers.Descriptors;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// DI registration for daemon-side LLM provider plugins.
/// </summary>
public static class LlmProviderServiceExtensions
{
    /// <summary>
    /// Registers all LLM provider plugins, the plugin factory, and the chat client provider.
    /// </summary>
    public static IServiceCollection AddLlmProviders(
        this IServiceCollection services,
        Dictionary<string, ProviderEntry> providers,
        ModelSelection models)
    {
        // Register descriptors (shared with CLI)
        services.AddProviderDescriptors();

        // Register daemon-specific plugins
        services.AddSingleton<OllamaProviderPlugin>();
        services.AddSingleton<OpenAiProviderPlugin>();
        services.AddSingleton<AnthropicProviderPlugin>();
        services.AddSingleton<OpenRouterProviderPlugin>();

        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OllamaProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenAiProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<AnthropicProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenRouterProviderPlugin>());

        // Register the plugin factory and chat client provider
        services.AddSingleton(sp =>
            new ProviderPluginFactory(providers, sp.GetServices<ILlmProviderPlugin>()));
        services.AddSingleton<IChatClientProvider>(sp =>
            new NetclawChatClientProvider(sp.GetRequiredService<ProviderPluginFactory>(), models));

        return services;
    }
}
