// -----------------------------------------------------------------------
// <copyright file="ProviderDescriptorServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Configuration.Http;
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers;

/// <summary>
/// DI registration for provider descriptors. Used by both CLI and daemon.
/// </summary>
public static class ProviderDescriptorServiceExtensions
{
    private const string HttpClientName = "ProviderProbe";
    private const string CopilotTokenClientName = "CopilotTokenExchange";

    /// <summary>
    /// Registers all provider descriptors and the registry.
    /// Uses a named HttpClient from the factory so descriptors are properly
    /// singleton-scoped without captive dependency issues.
    /// </summary>
    public static IServiceCollection AddProviderDescriptors(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName).AddNetclawHeaders("provider-probe");
        services.AddHttpClient(CopilotTokenClientName).AddNetclawHeaders("copilot-token");

        // CopilotTokenExchanger is shared between CLI (for probe) and daemon
        // (for chat). The in-memory cache is per-process, which is what we want.
        services.AddSingleton(sp => new CopilotTokenExchanger(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(CopilotTokenClientName),
            sp.GetService<TimeProvider>()));

        services.AddSingleton(sp =>
            ProviderDescriptorCatalog.Create(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                sp.GetRequiredService<CopilotTokenExchanger>(),
                sp.GetService<TimeProvider>()));

        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Ollama);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenAiCompatible);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenAi);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Anthropic);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenRouter);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().GitHubCopilot);
        services.AddSingleton(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().VeniceAi);

        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Ollama);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenAiCompatible);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenAi);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().Anthropic);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().OpenRouter);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().GitHubCopilot);
        services.AddSingleton<IProviderDescriptor>(sp => sp.GetRequiredService<ProviderDescriptorCatalog>().VeniceAi);

        services.AddSingleton(sp =>
            new ProviderDescriptorRegistry(sp.GetRequiredService<ProviderDescriptorCatalog>().All));
        services.AddSingleton<IProviderProbe>(sp => sp.GetRequiredService<ProviderDescriptorRegistry>());

        return services;
    }
}
