using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration.Providers.Descriptors;

namespace Netclaw.Configuration.Providers;

/// <summary>
/// DI registration for provider descriptors. Used by both CLI and daemon.
/// </summary>
public static class ProviderDescriptorServiceExtensions
{
    /// <summary>
    /// Registers all provider descriptors and the registry.
    /// Each descriptor gets its own typed <see cref="HttpClient"/> via <c>AddHttpClient</c>.
    /// </summary>
    public static IServiceCollection AddProviderDescriptors(this IServiceCollection services)
    {
        services.AddHttpClient<OllamaDescriptor>();
        services.AddHttpClient<OpenAiDescriptor>();
        services.AddHttpClient<AnthropicDescriptor>();
        services.AddHttpClient<OpenRouterDescriptor>();

        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<OllamaDescriptor>());
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<OpenAiDescriptor>());
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<AnthropicDescriptor>());
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<OpenRouterDescriptor>());

        services.AddSingleton<ProviderDescriptorRegistry>();
        services.AddSingleton<IProviderProbe>(sp => sp.GetRequiredService<ProviderDescriptorRegistry>());

        return services;
    }
}
