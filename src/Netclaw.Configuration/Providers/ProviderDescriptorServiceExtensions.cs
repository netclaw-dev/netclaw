using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration.Providers.Descriptors;

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

        services.AddSingleton<OllamaDescriptor>(sp =>
            new OllamaDescriptor(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        services.AddSingleton<OpenAiDescriptor>(sp =>
            new OpenAiDescriptor(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        services.AddSingleton<AnthropicDescriptor>(sp =>
            new AnthropicDescriptor(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        services.AddSingleton<OpenRouterDescriptor>(sp =>
            new OpenRouterDescriptor(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<OllamaDescriptor>());
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<OpenAiDescriptor>());
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<AnthropicDescriptor>());
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<OpenRouterDescriptor>());

        services.AddSingleton<ProviderDescriptorRegistry>();
        services.AddSingleton<IProviderProbe>(sp => sp.GetRequiredService<ProviderDescriptorRegistry>());

        return services;
    }
}
