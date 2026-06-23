// -----------------------------------------------------------------------
// <copyright file="LlmProviderServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Configuration.Http;
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Netclaw.Providers.VeniceAi;

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

        services.AddProviderOAuthServices();

        // Register daemon-specific plugins
        services.AddSingleton<OllamaProviderPlugin>();
        services.AddSingleton<OpenAiCompatibleProviderPlugin>();
        services.AddSingleton<OpenAiProviderPlugin>();
        services.AddSingleton<AnthropicProviderPlugin>();
        services.AddSingleton<OpenRouterProviderPlugin>();
        services.AddSingleton<GitHubCopilotProviderPlugin>();
        services.AddSingleton<VeniceAiProviderPlugin>();

        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OllamaProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenAiCompatibleProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenAiProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<AnthropicProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<OpenRouterProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<GitHubCopilotProviderPlugin>());
        services.AddSingleton<ILlmProviderPlugin>(sp => sp.GetRequiredService<VeniceAiProviderPlugin>());

        return services;
    }

    /// <summary>
    /// Registers provider OAuth device-flow, refresh, and refresh-aware probe services.
    /// Call after <see cref="ProviderDescriptorServiceExtensions.AddProviderDescriptors"/>
    /// when the host needs OAuth provider flows or configured-provider refresh.
    /// </summary>
    public static IServiceCollection AddProviderOAuthServices(this IServiceCollection services)
    {
        services.AddHttpClient("OAuthDeviceFlow").AddNetclawHeaders("provider-oauth");
        services.AddSingleton(sp =>
            new OAuthDeviceFlowService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                sp.GetService<TimeProvider>()));
        services.AddSingleton(sp =>
            new OpenAiDeviceFlowService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<DeviceFlowServiceFactory>();
        services.AddSingleton(sp => new ProviderOAuthTokenRefreshService(
            sp.GetRequiredService<NetclawPaths>(),
            sp.GetRequiredService<DeviceFlowServiceFactory>(),
            sp.GetService<IOperationalNotificationSink>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton<ProviderOAuthRefreshingProbe>();
        services.AddSingleton<IProviderProbe>(sp => sp.GetRequiredService<ProviderOAuthRefreshingProbe>());

        return services;
    }
}
