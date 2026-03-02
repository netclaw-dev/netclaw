using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Configuration.Providers;

/// <summary>
/// DI registration for provider descriptors. Used by both CLI and daemon.
/// </summary>
public static class ProviderDescriptorServiceExtensions
{
    private const string HttpClientName = "ProviderProbe";

    /// <summary>
    /// Registers all provider descriptors and the registry.
    /// Uses a named HttpClient from the factory so descriptors are properly
    /// singleton-scoped without captive dependency issues.
    /// </summary>
    public static IServiceCollection AddProviderDescriptors(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName);

        services.AddSingleton(sp =>
            ProviderDescriptorCatalog.Create(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Ollama);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenAi);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Anthropic);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenRouter);

        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Ollama);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenAi);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Anthropic);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenRouter);

        services.AddSingleton(sp =>
            new ProviderDescriptorRegistry(sp.GetRequiredService<ProviderDescriptorCatalog>().All));
        services.AddSingleton<IProviderProbe>(sp => sp.GetRequiredService<ProviderDescriptorRegistry>());

        return services;
    }
}
