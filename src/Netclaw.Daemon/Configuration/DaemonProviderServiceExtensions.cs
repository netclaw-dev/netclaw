using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Daemon-level provider wiring: plugin factory, retry, resilient decorator.
/// Chains on top of <see cref="LlmProviderServiceExtensions.AddLlmProviders"/>.
/// </summary>
public static class DaemonProviderServiceExtensions
{
    /// <summary>
    /// Registers provider plugins (via Netclaw.Providers) plus daemon-specific
    /// factory, retry, and resilient chat client provider.
    /// </summary>
    public static IServiceCollection AddDaemonLlmProviders(
        this IServiceCollection services,
        Dictionary<string, ProviderEntry> providers,
        ModelSelection models)
    {
        // Register plugins and OAuth from Netclaw.Providers
        services.AddLlmProviders();

        // Register the plugin factory and chat client provider
        services.AddSingleton(sp =>
            new ProviderPluginFactory(providers, sp.GetServices<ILlmProviderPlugin>()));

        // Retry policy (TODO: make configurable via netclaw.json Resilience section)
        services.AddSingleton(new RetryPolicy());

        // Raw provider → Resilient decorator (Logging → Retry → Failover)
        services.AddSingleton<IChatClientProvider>(sp =>
        {
            var raw = new NetclawChatClientProvider(
                sp.GetRequiredService<ProviderPluginFactory>(), models);
            return new ResilientChatClientProviderDecorator(
                raw,
                sp.GetRequiredService<RetryPolicy>(),
                models,
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetService<TimeProvider>());
        });

        return services;
    }
}
