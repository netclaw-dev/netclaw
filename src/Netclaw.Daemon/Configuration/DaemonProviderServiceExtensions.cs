// -----------------------------------------------------------------------
// <copyright file="DaemonProviderServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Daemon-level provider wiring: plugin factory, retry, composed pipelines, and a
/// router-backed chat client provider. Chains on top of
/// <see cref="LlmProviderServiceExtensions.AddLlmProviders"/>.
/// </summary>
public static class DaemonProviderServiceExtensions
{
    /// <summary>
    /// Registers provider plugins (via Netclaw.Providers) plus the daemon-specific
    /// plugin factory, retry policy, pipeline composition, and routing.
    /// </summary>
    public static IServiceCollection AddDaemonLlmProviders(
        this IServiceCollection services,
        Dictionary<string, ProviderEntry> providers,
        ModelSelection models,
        RetryPolicy? retryPolicy = null)
    {
        // Register plugins and OAuth from Netclaw.Providers
        services.AddLlmProviders();

        // Raw provider client factory (raw client + vendor options per model)
        services.AddSingleton(sp =>
            new ProviderPluginFactory(providers, sp.GetServices<ILlmProviderPlugin>()));

        // Transport retry budget/backoff. The RetryingChatClient layer is the single
        // owner of LLM transient-failure retry; this is its configured policy
        // (Session:Tuning:StreamingRetryPolicy), defaulting to the standard policy.
        services.AddSingleton(retryPolicy ?? new RetryPolicy());

        // Composes the cross-cutting middleware (Logging → Retry) around each provider
        // pipeline via ChatClientBuilder.
        services.AddSingleton(sp => new PipelineChatClientFactory(
            sp.GetRequiredService<ProviderPluginFactory>(),
            sp.GetRequiredService<RetryPolicy>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<TimeProvider>()));

        // Routing policy. Today: role-based selection with primary→fallback failover.
        // Per-session / per-provider routing slots in here later as a different policy.
        services.AddSingleton<IChatClientRouter>(sp => new RoleBasedFailoverRouter(
            sp.GetRequiredService<PipelineChatClientFactory>(), models));

        // Router-backed provider the actor layer consumes via GetClient(role).
        services.AddSingleton<IChatClientProvider>(sp => new RoutingChatClientProvider(
            sp.GetRequiredService<IChatClientRouter>(),
            sp.GetRequiredService<IOperationalNotificationSink>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
