// -----------------------------------------------------------------------
// <copyright file="LlmProviderServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.OAuth;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers;

/// <summary>
/// DI registration for LLM provider plugins and OAuth services.
/// </summary>
public static class LlmProviderServiceExtensions
{
    /// <summary>
    /// Registers all LLM provider plugins and OAuth services.
    /// Call from Daemon after <see cref="ProviderDescriptorServiceExtensions.AddProviderDescriptors"/>.
    /// </summary>
    public static IServiceCollection AddLlmProviders(this IServiceCollection services)
    {
        // Register descriptors (shared with CLI)
        services.AddProviderDescriptors();

        // OAuth device flow services (for future auto-refresh)
        services.AddHttpClient("OAuthDeviceFlow");
        services.AddSingleton(sp =>
            new OAuthDeviceFlowService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                sp.GetService<TimeProvider>()));
        services.AddSingleton(sp =>
            new OpenAiDeviceFlowService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<DeviceFlowServiceFactory>();

        // Register daemon-specific plugins
        services.AddSingleton<OllamaProviderPlugin>();
        services.AddSingleton<OpenAiCompatibleProviderPlugin>();
        services.AddSingleton<OpenAiProviderPlugin>();
        services.AddSingleton<AnthropicProviderPlugin>();
        services.AddSingleton<OpenRouterProviderPlugin>();

        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OllamaProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenAiCompatibleProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenAiProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<AnthropicProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenRouterProviderPlugin>());

        return services;
    }
}
