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
    /// plugin factory, retry policy, pipeline composition, and routing. When
    /// <paramref name="validation"/> reports
    /// <see cref="ProviderRuntimeStatus.NoProviderConfigured"/>, the No-Op chat
    /// client provider is registered instead so the host starts in degraded mode.
    /// </summary>
    public static IServiceCollection AddDaemonLlmProviders(
        this IServiceCollection services,
        Dictionary<string, ProviderEntry> providers,
        ModelSelection models,
        ProviderRuntimeValidation validation,
        RetryPolicy? retryPolicy = null)
    {
        // Register descriptors/OAuth endpoints even in degraded mode so operators
        // can recover through provider/model setup flows without restarting first.
        services.AddLlmProviders();

        if (validation.Status == ProviderRuntimeStatus.NoProviderConfigured)
        {
            services.AddSingleton<IChatClientProvider>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Netclaw.ChatClient");
                logger.LogWarning(
                    "No valid inference provider configured ({Reason}). Registering No-Op chat client. Run `netclaw doctor` for details.",
                    validation.Reason);
                return new NoOpChatClientProvider(validation.AvailableProviders);
            });
            return services;
        }

        if (validation.Status == ProviderRuntimeStatus.Invalid)
        {
            // Fail loudly with the validation reason rather than letting the
            // provider plugin factory throw a raw "Provider 'X' not found"
            // deep in the DI graph. The exception fires when
            // IChatClientProvider is first resolved so it surfaces during the
            // host's startup sequence, not at config-binding time.
            services.AddSingleton<IChatClientProvider>(_ =>
                throw new InvalidOperationException(
                    $"Invalid inference configuration: {validation.Reason}. " +
                    "Fix the issue in `netclaw.json` and restart the daemon. Run `netclaw doctor` for details."));
            return services;
        }

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
