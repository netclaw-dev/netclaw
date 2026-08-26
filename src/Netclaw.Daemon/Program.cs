// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Threading.RateLimiting;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Akka.Persistence.Sql.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Configuration.Http;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Netclaw.Configuration.Secrets;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Providers;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Lifecycle;
using Netclaw.Daemon.Reminders;
using Netclaw.Daemon.Skills;
using Netclaw.Daemon.Webhooks;
using Netclaw.Embeddings;
using Netclaw.Search;
using Netclaw.Tools;
using Netclaw.Security;
using static Microsoft.Extensions.Logging.LogLevel;

// Handled first, before any directory creation, lock-file acquisition, or host startup:
// `netclawd --version`/`-v` must print the version and exit rather than booting a real
// daemon instance (alpha.onnx.2 production canary regression).
if (DaemonCliArgs.IsVersionRequest(args))
{
    // Fully qualified: Program.cs (top-level statements) sits in the global namespace, and both
    // Netclaw.Daemon and Netclaw.Configuration are `using`-imported here, so the unqualified
    // "BuildInfo" is ambiguous between the two. Netclaw.Daemon.BuildInfo is the daemon-specific
    // facade that reads the daemon assembly's own metadata (see that type's remarks).
    Console.WriteLine(
        $"netclawd {Netclaw.Daemon.BuildInfo.FullVersion} "
        + $"(commit {Netclaw.Daemon.BuildInfo.CommitHash}, built {Netclaw.Daemon.BuildInfo.BuildTimestamp})");
    return;
}

var bootstrapPaths = new NetclawPaths();
try
{
    bootstrapPaths.EnsureDirectoriesExist();
}
catch (NetclawDirectoryInitializationException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}

using var crashMonitor = DaemonCrashMonitor.Register(
    bootstrapPaths,
    benignUnobservedFilters: [KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace]);

try
{
    // Acquire an exclusive lock file before anything else. This is the OS-level
    // singleton guard — a second netclawd instance will fail to open the file
    // and exit immediately. The lock survives soft restarts (same process) and
    // is released by the OS on crash (fd closed → flock released).

    FileStream lockFile;
    try
    {
        lockFile = new FileStream(
            bootstrapPaths.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }
    catch (IOException)
    {
        Console.Error.WriteLine(
            "error: Another netclawd instance is already running (lock file held). Exiting.");
        Environment.ExitCode = 1;
        return;
    }

    await using (lockFile)
    {
        var restartSignal = new DaemonRestartSignal();
        do
        {
            restartSignal.Reset();
            // Each pass through the loop is a new daemon generation; the counter is
            // surfaced on /api/health/ready so the init wizard can confirm a config
            // reload actually restarted the daemon (#1302).
            restartSignal.AdvanceGeneration();
            await RunDaemonAsync(args, restartSignal, crashMonitor, bootstrapPaths);
        } while (restartSignal.RestartRequested);
    }
}
catch (NetclawDirectoryInitializationException ex)
{
    crashMonitor.RecordTopLevelException(ex);
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    crashMonitor.RecordTopLevelException(ex);
    throw;
}

static async Task RunDaemonAsync(
    string[] args,
    DaemonRestartSignal restartSignal,
    DaemonCrashMonitor crashMonitor,
    NetclawPaths bootstrapPaths)
{
    // Anchor the process CWD to a durable user-owned directory.
    // Without this, the daemon runs from its install location (e.g. /usr/local/bin),
    // which means shell commands, relative file paths, and stdio MCP child processes
    // (Playwright screenshots, etc.) all default to a potentially privileged directory.
    // OS temp cleanup can remove a live process CWD, so this directory must stay
    // under the Netclaw home instead of the system temp directory.
    Environment.CurrentDirectory = bootstrapPaths.RuntimeDirectory;

    // Resolve inside each daemon generation. A soft restart is allowed to
    // select a newly installed compatible host, but one running generation
    // keeps this exact environment for parsing, authorization, and execution.
    var shellResolution = await ShellExecutionEnvironmentResolver
        .CreateDefault(TimeProvider.System)
        .ResolveAsync(ShellExecutionEnvironmentResolver.DetectCurrentPlatform());

    var builder = WebApplication.CreateBuilder(args);

    // Register process-lifetime restart signal so services can trigger a restart
    builder.Services.AddSingleton(restartSignal);

    // Load configuration first (netclaw.json, secrets.json, env vars) so that
    // DaemonConfig.Host/Port can be read before binding the WebHost URL.
    var paths = ConfigureConfigServices(builder.Services, builder.Configuration, bootstrapPaths);

    // Bind listen address from DaemonConfig; falls back to 127.0.0.1:5199 if
    // the Daemon section is absent from netclaw.json.
    var daemonConfig = DaemonConfig.BindFromConfiguration(builder.Configuration.GetSection("Daemon"));
    builder.WebHost.UseUrls($"http://{daemonConfig.Host}:{daemonConfig.Port}");
    var daemonLogLevel = builder.ConfigureNetclawLogging(paths);
    builder.AddNetclawTelemetry();
    ConfigureDaemonServices(
        builder.Services,
        builder.Configuration,
        paths,
        daemonLogLevel,
        daemonConfig,
        shellResolution);

    // Authentication — a PolicyScheme selector is the default scheme.
    // It routes to DeviceBearer when an Authorization: Bearer header is present,
    // otherwise to Loopback (local operator).  This ensures [Authorize] endpoints
    // are reachable by both loopback clients and paired remote devices.
    builder.Services.AddSingleton<DeviceRegistry>();
    builder.Services.AddSingleton<BootstrapStateStore>();
    builder.Services.AddSingleton<BootstrapDeviceSeeder>();
    builder.Services.AddSingleton<PairingCodeService>();
    builder.Services.AddSingleton<PairingExchangeGuard>();
    builder.Services.AddSingleton<IRemoteAuthSchemeRegistration, DevicePairingSchemeRegistration>();
    builder.Services.AddNetclawAuthSchemes(daemonConfig);
    builder.Services.AddAuthorization();

    // Add OpenAPI
    builder.Services.AddOpenApi();

    // Rate limiting for the unauthenticated pairing exchange endpoint.
    // 5 attempts per minute per IP — brute-force defense for the 8-char code space.
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("pairing-exchange", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }));
        options.RejectionStatusCode = 429;
    });
    builder.Services.AddMattermostActionEndpointRateLimiting();

    // SignalR for remote clients (CLI thin client, Blazor ops console)
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<SessionCatalogService>();
    builder.Services.AddSingleton<ISessionLifecycleObserver>(sp => sp.GetRequiredService<SessionCatalogService>());
    builder.Services.AddSingleton<ClaimsPrincipalMapper>();
    builder.Services.AddSingleton<SessionRegistry>();
    builder.Services.AddSingleton<DaemonStartClock>();
    builder.Services.AddSingleton<DaemonRuntimeStatusService>();
    builder.Services.AddSingleton<DailyStatsPublisher>();
    builder.Services.AddSingleton<Netclaw.Actors.Telemetry.ISessionMetrics>(sp => sp.GetRequiredService<DailyStatsPublisher>());
    builder.Services.AddSingleton<DaemonStatsService>();
    builder.Services.AddSingleton<SessionIngressGate>();
    builder.Services.AddSingleton<RestartManifestStore>();
    builder.Services.AddSingleton<DaemonRestartCoordinator>();
    builder.Services.AddSingleton<IDaemonRestartCoordinator>(sp => sp.GetRequiredService<DaemonRestartCoordinator>());

    var app = builder.Build();
    crashMonitor.AttachServices(app.Services);

    var startupLogger = app.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Netclaw.Startup");
    startupLogger.LogInformation(
        "Selected native shell {Executable} at {ExecutablePath} with {Grammar} grammar and {Dialect} dialect.",
        shellResolution.Environment.ExecutableName,
        shellResolution.Environment.ExecutablePath,
        shellResolution.Environment.Grammar,
        shellResolution.Environment.PowerShellDialect?.ToString() ?? "not-applicable");
    if (shellResolution.FallbackReason is { } fallbackReason)
    {
        startupLogger.LogWarning(
            "Selected Windows PowerShell 5.1 fallback because {FallbackReason}; rejected preferred version: {RejectedVersion}.",
            fallbackReason,
            shellResolution.RejectedPreferredVersion?.ToString() ?? "not-found");
    }

    if (daemonConfig.ExposureMode == ExposureMode.ReverseProxy)
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        foreach (var trustedProxy in DaemonExposureValidator.ParseTrustedProxies(daemonConfig.TrustedProxies))
        {
            if (trustedProxy.PrefixLength is null)
            {
                forwardedHeadersOptions.KnownProxies.Add(trustedProxy.Address);
            }
            else
            {
                forwardedHeadersOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(trustedProxy.Address, trustedProxy.PrefixLength.Value));
            }
        }

        // Forwarded headers are only meaningful after the direct peer is verified as a trusted proxy.
        app.UseForwardedHeaders(forwardedHeadersOptions);
    }

    // Eagerly resolve so StartedAt reflects daemon startup, not first request.
    app.Services.GetRequiredService<DaemonStartClock>();

    // Eagerly resolve so capability auto-detection (HF / OpenRouter / provider
    // probes) runs at startup, not on first session creation — preserving the
    // timing of the previous eager-resolution path while letting detection use
    // the host's IModelCapabilityResolver chain and ILoggerFactory.
    app.Services.GetRequiredService<ModelCapabilities>();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    // Require authorization for the OpenAPI document so the full API surface is not
    // exposed to unauthenticated callers when the daemon binds to a non-loopback
    // address (e.g. ExposureMode.ReverseProxy). Loopback callers are still served:
    // the AuthSelector routes them to LoopbackAuthenticationHandler, which issues an
    // authenticated Operator ticket that satisfies the default policy.
    app.MapOpenApi().RequireAuthorization();

    // Gateway surface
    app.MapHub<SessionHub>("/hub/session");
    app.MapGet("/api/health/ready", (DaemonRestartSignal restartSignal, HttpResponse response) =>
        {
            // Surface the monotonic restart generation on the anonymous readiness probe
            // (#1302). The init wizard reads it to confirm the daemon it is polling is the
            // post-config-reload generation, not the still-draining pre-restart one — on
            // the anonymous endpoint precisely so first-init needs no auth token.
            response.Headers["X-Netclaw-Generation"] =
                restartSignal.Generation.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Ok("healthy");
        })
        .WithName("HealthReady")
        .WithSummary("Liveness probe reporting the daemon is accepting requests.")
        .WithTags("Health");
    app.MapGet("/api/health/status", async ValueTask<Ok<DaemonRuntimeStatus.Response>> (DaemonRuntimeStatusService statusService, CancellationToken cancellationToken) =>
        TypedResults.Ok(await statusService.GetStatusAsync(cancellationToken)))
        .WithName("GetHealthStatus")
        .WithSummary("Get the daemon's runtime status, including connector health.")
        .WithTags("Health")
        .RequireAuthorization();
    app.MapGet("/api/sessions", (SessionCatalogService catalog, int? limit, int? offset) =>
        TypedResults.Ok(catalog.ListRecent(limit ?? 50, offset ?? 0)))
        .WithName("ListSessions")
        .WithSummary("List the most recent sessions.")
        .WithTags("Sessions")
        .RequireAuthorization();
    app.MapGet("/api/stats", async ValueTask<Ok<DaemonStats.Response>> (DaemonStatsService statsService, int? days, CancellationToken ct) =>
        TypedResults.Ok(await statsService.GetStatsAsync(days, ct)))
        .WithName("GetStats")
        .WithSummary("Get daemon usage statistics over the requested window.")
        .WithTags("Stats")
        .RequireAuthorization();
    app.MapGet("/api/stats/skills", async ValueTask<Ok<SkillUsageStats.Response>> (DaemonStatsService statsService, int? days, CancellationToken ct) =>
        TypedResults.Ok(await statsService.GetSkillUsageStatsAsync(days, ct)))
        .WithName("GetSkillUsageStats")
        .WithSummary("Get per-skill usage statistics over the requested window.")
        .WithTags("Stats")
        .RequireAuthorization();
    app.MapWebhookEndpoints();
    app.MapWebhookRouteEndpoints();
    app.MapMattermostActionEndpoint();

    app.MapPairingEndpoints();

    app.MapMcpEndpoints();

    app.MapSkillEndpoints();

    app.MapProviderOAuthEndpoints();

    app.MapLifecycleEndpoints();

    // Register tools that need DI-resolved dependencies after the container is built.
    ChannelToolRegistration.RegisterChannelTools(app.Services);
    SkillToolRegistration.RegisterSkillTools(app.Services);

    // Reminder REST API
    app.MapReminderEndpoints();

    // Fire startup notification after all hosted services are ready
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            app.Services.GetRequiredService<DaemonLifecycleNotifier>().NotifyStarted();
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write(ex, "daemon-started-hook");
        }
    });

    try
    {
        await app.RunAsync();
    }
    finally
    {
        crashMonitor.DetachServices();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Shared configuration services
// ═══════════════════════════════════════════════════════════════════════

static NetclawPaths ConfigureConfigServices(
    IServiceCollection services,
    IConfigurationManager configuration,
    NetclawPaths bootstrapPaths)
{
    // Initialize Data Protection for secrets encryption/decryption.
    // Must happen before config binding so SensitiveStringTypeConverter
    // can transparently decrypt ENC: values.
    var protector = SecretsProtection.CreateProtector(bootstrapPaths);
    services.AddSingleton<ISecretsProtector>(protector);
    SensitiveStringTypeConverter.Protector = protector;

    // Layered configuration chain:
    // 1. netclaw.json (base config, optional)
    // 2. secrets.json (credentials overlay, optional)
    // 3. NETCLAW_* environment variables (highest priority)
    configuration
        .AddJsonFile(bootstrapPaths.NetclawConfigPath, optional: true, reloadOnChange: false)
        .AddJsonFile(bootstrapPaths.SecretsPath, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("NETCLAW_");

    // Re-create paths with config-driven overrides (e.g. custom workspaces directory).
    var workspacesDir = configuration.GetValue<string>("Workspaces:Directory");
    var paths = new NetclawPaths(bootstrapPaths.BasePath, workspacesDir);
    paths.EnsureDirectoriesExist();
    services.AddSingleton(paths);

    // TimeProvider (virtualized for testing)
    services.AddSingleton(TimeProvider.System);

    // Providers and model resolution via plugin architecture.
    // No silent fallback to local-ollama: an empty Providers section yields
    // the NoProviderConfigured outcome and the host registers NoOpChatClientProvider.
    var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));
    var models = ModelConfigurationResolver.Resolve(configuration).Selection;
    var validation = ProviderRuntimeValidation.Evaluate(
        providers,
        models,
        ProviderRuntimeConfiguration.FromConfiguration(configuration));

    // The transport RetryingChatClient is the single owner of LLM transient-failure
    // retry, so it uses the configured streaming-retry budget.
    var streamingRetryPolicy = SessionConfig
        .BindFromConfiguration(configuration.GetSection("Session"))
        .Tuning.StreamingRetryPolicy;

    services.AddSingleton(validation);
    services.AddDaemonLlmProviders(providers, models, validation, streamingRetryPolicy);

    return paths;
}

// ═══════════════════════════════════════════════════════════════════════
// Daemon-only services (actor system, tools, persistence)
// ═══════════════════════════════════════════════════════════════════════

static void ConfigureDaemonServices(
    IServiceCollection services,
    IConfigurationManager configuration,
    NetclawPaths paths,
    LogLevel daemonLogLevel,
    DaemonConfig daemonConfig,
    ShellEnvironmentResolution shellResolution)
{
    var shellEnvironment = shellResolution.Environment;
    services.AddSingleton(shellEnvironment);

    // Daemon bind address and exposure mode (computed once in RunDaemonAsync)
    services.AddSingleton(daemonConfig);

    // Validate tunnel prerequisites before the rest of the daemon starts.
    // Throws from StartAsync to abort startup if the required process is missing.
    services.AddHostedService<ExposureModeValidationService>();
    services.AddHostedService<BootstrapCompletionMarkerService>();

    var resolvedModels = ModelConfigurationResolver.Resolve(configuration).Selection;
    services
        .AddOptions<ModelSelection>()
        .Configure(options =>
        {
            options.Main = resolvedModels.Main;
            options.Fallback = resolvedModels.Fallback;
            options.Compaction = resolvedModels.Compaction;
        })
        .ValidateOnStart();
    services.AddSingleton<IValidateOptions<ModelSelection>, ModelSelectionValidator>();
    services
        .AddOptions<DaemonPersistenceOptions>()
        .Bind(configuration.GetSection("Persistence"))
        .ValidateOnStart();
    services.AddSingleton<IValidateOptions<DaemonPersistenceOptions>, DaemonPersistenceOptionsValidator>();
    var persistence = configuration.GetSection("Persistence")
        .Get<DaemonPersistenceOptions>() ?? new DaemonPersistenceOptions();
    services.AddSingleton(persistence);

    services.Configure<HostOptions>(options =>
    {
        // Generic-host shutdown ceiling for all hosted services combined. Kept in lockstep
        // with DaemonConfig.SystemdTimeoutStopSec (= GracefulShutdownBudget + teardown margin)
        // rather than a bare, unrelated literal: a value shorter than the Akka
        // before-service-unbind phase timeout below would silently reintroduce the same class
        // of mismatch behind netclaw-dev/netclaw#1665 (budgets that look independent but must
        // stay ordered). AkkaHostedService.StopAsync does not observe this cancellation token
        // and awaits CoordinatedShutdown.Run to its own natural completion, so this value does
        // not itself truncate the drain — it only keeps the documented layering consistent.
        options.ShutdownTimeout = DaemonConfig.SystemdTimeoutStopSec;
    });

    // Resolve models for session config
    var models = resolvedModels;
    services.AddSingleton(models);

    // Auto-detect model capabilities via the runtime IModelCapabilityResolver
    // chain (registered further down). Lazy factory so detection runs against
    // the real DI-wired resolvers with the host's logger — no temp HttpClient
    // / LoggerFactory needed, and per-resolver Debug output is visible. The
    // factory is invoked eagerly after Build() (see RunDaemonAsync) so timing
    // matches a startup-bound resolution rather than first-session lazy hit.
    // Capability resolution runs against the loaded providers as-is — if
    // no providers are configured we never reach a real plugin, the No-Op
    // client supersedes, and capabilities default to text-only below.
    var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));
    var mainProviderType = providers.TryGetValue(models.Main.Provider, out var mainProvider)
        ? mainProvider.Type
        : null;
    var ollamaEndpoint = mainProviderType?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true
        ? (string.IsNullOrWhiteSpace(mainProvider!.Endpoint)
            ? OllamaDescriptor.DefaultEndpointValue
            : mainProvider.Endpoint)
        : null;
    var openAiCompatibleEndpoint = mainProviderType?.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) == true
        ? (string.IsNullOrWhiteSpace(mainProvider!.Endpoint)
            ? "http://localhost:11434"
            : mainProvider.Endpoint)
        : null;
    var openAiCompatibleApiKey = mainProviderType?.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) == true
        ? mainProvider?.ApiKey?.Value
        : null;

    services.AddSingleton<ModelCapabilities>(sp =>
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Netclaw.Startup");

        // In degraded mode (No-Op chat client) we never talk to a real model;
        // skip capability detection (which may do network I/O) and use defaults.
        var chatProvider = sp.GetRequiredService<IChatClientProvider>();
        if (chatProvider.IsDegraded)
        {
            logger.LogInformation(
                "No-Op chat client active; skipping model capability detection and using text-only defaults.");
            return ModelCapabilityResolution.ResolveModelCapabilities(models, detected: null);
        }

        var resolver = sp.GetRequiredService<IModelCapabilityResolver>();
        var detected = resolver.ResolveAsync(models.Main.ModelId, CancellationToken.None)
            .GetAwaiter().GetResult();
        var resolved = ModelCapabilityResolution.ResolveModelCapabilities(models, detected, logger: logger);

        // Report the *effective* capabilities the runtime will use, with per-field
        // provenance — not the raw detector output. Precedence mirrors
        // ModelCapabilityResolution: configured override > detected > default. The
        // previous version logged the detected values, so setting an InputModalities
        // override on a provider that reports no modalities but does report a context
        // window (e.g. vLLM) printed "input=unknown" and looked like the override had
        // been ignored, even though it was applied.
        var inputSource = models.Main.InputModalities is not null ? "configured"
            : detected?.InputModalities is not null ? "detected" : "default";
        var outputSource = models.Main.OutputModalities is not null ? "configured"
            : detected?.OutputModalities is not null ? "detected" : "default";
        var contextSource = models.Main.ContextWindow is not null ? "configured"
            : detected?.ContextWindowTokens is > 0 ? "detected" : "default";

        logger.LogInformation(
            "Resolved model capabilities for {ModelId}: input={Input} ({InputSource}), "
            + "output={Output} ({OutputSource}), context_window={ContextWindow} ({ContextSource})",
            models.Main.ModelId,
            resolved.InputModalities, inputSource,
            resolved.OutputModalities, outputSource,
            resolved.ContextWindowTokens, contextSource);

        // Nudge for the common trap: a multimodal model behind a provider that
        // advertises no modality metadata runs text-only until an operator sets the
        // override. Fires only when nothing supplied input modalities.
        if (inputSource == "default")
        {
            logger.LogInformation(
                "{ModelId} resolved to text-only input; no modality data came from the provider or "
                + "capability oracles. If this model accepts images, set Models:Main:InputModalities "
                + "to \"Text, Image\" to enable vision.",
                models.Main.ModelId);
        }

        return resolved;
    });

    // Session config: bind operator-facing settings from config section
    var sessionConfig = SessionConfig.BindFromConfiguration(configuration.GetSection("Session"));
    services.AddSingleton(sessionConfig);

    // Tools (auto-bound, no required properties)
    var toolConfig = configuration.GetSection("Tools")
        .Get<ToolConfig>() ?? new ToolConfig();
    var attachmentErrors = toolConfig.AudienceProfiles.ValidateChannelAttachments();
    if (attachmentErrors.Count > 0)
    {
        throw new InvalidOperationException(
            "Invalid Tools.AudienceProfiles.ChannelAttachments configuration: "
            + string.Join("; ", attachmentErrors));
    }
    services.AddSingleton(toolConfig);

    var securityPolicyConfig = configuration.GetSection("Security")
        .Get<SecurityPolicyConfig>() ?? new SecurityPolicyConfig();
    services.AddSingleton(securityPolicyConfig);
    var effectivePolicyDefaults = SecurityPolicyDefaults.Resolve(securityPolicyConfig);
    services.AddSingleton(effectivePolicyDefaults);
    services.AddSingleton<TrustContextDeriver>();

    // Reminder limits stay private. Netclaw sets the library acknowledgement
    // lease in WithReminderManager because an LLM attempt can take one hour.
    services.AddSingleton<ReminderDefinitionStore>();
    services.AddSingleton<ReminderHistoryStore>();

    // Background jobs — infrastructure singleton for async shell execution
    services.AddSingleton<Netclaw.Actors.Jobs.BackgroundJobDefinitionStore>();

    var webhooksConfig = configuration.GetSection("Webhooks")
        .Get<WebhooksConfig>() ?? new WebhooksConfig();
    services.AddSingleton(webhooksConfig);
    var webhookRouteStore = new Netclaw.Configuration.WebhookRouteStore(paths);
    services.AddSingleton(webhookRouteStore);
    services.AddSingleton<WebhookRouteCatalog>();
    services.AddSingleton<WebhookRequestVerifier>();
    services.AddSingleton<WebhookIngressGuard>();
    services.AddSingleton<WebhookExecutionService>();
    services.AddSingleton<IWebhookExecutionService>(sp => sp.GetRequiredService<WebhookExecutionService>());

    // Search backend selection — gated on SearchConfig.Enabled
    var searchConfig = configuration.GetSection("Search")
        .Get<SearchConfig>() ?? new SearchConfig();
    var searchBackend = searchConfig.Enabled ? CreateSearchBackend(searchConfig) : null;

    // Agent tools cannot read or change the control plane. This also protects the
    // complete operator-only tool catalogs from model-visible name disclosure.
    var toolPathPolicy = DaemonToolPathPolicyFactory.Create(paths, shellEnvironment);
    services.AddSingleton(toolPathPolicy);

    // Load operator-authored hard-deny overrides (additive only — see
    // HardDenyOverridesLoader). Missing file → empty list and only shipped
    // defaults apply. Malformed file → daemon refuses to start; the
    // loader throws InvalidDataException with operator-facing context so
    // the failure surfaces loudly rather than silently dropping rules.
    var hardDenyOverridesLoader = new HardDenyOverridesLoader();
    var hardDenyOverrides = hardDenyOverridesLoader.Load(paths.HardDenyOverridesPath);
    services.AddSingleton(hardDenyOverridesLoader);

    var shellCommandPolicy = new ShellCommandPolicy(
        shellEnvironment,
        toolConfig.HardDenyPatterns,
        hardDenyOverrides);
    services.AddSingleton(shellCommandPolicy);

    services.AddShellParser(shellEnvironment);

    // Subagent timeout configuration
    var subAgentConfig = configuration.GetSection("SubAgents")
        .Get<SubAgentConfig>() ?? new SubAgentConfig();
    services.AddSingleton(subAgentConfig);

    // Cross-session memory: provider-based wiring
    var memoryConfig = configuration.GetSection("Memory")
        .Get<MemoryConfig>() ?? new MemoryConfig();
    services.AddSingleton(memoryConfig);

    // System skill sync behavior
    var skillSyncConfig = configuration.GetSection("SkillSync")
        .Get<SkillSyncConfig>() ?? new SkillSyncConfig();
    services.AddSingleton(skillSyncConfig);

    // Scheduling / reminders subsystem kill switch
    var schedulingConfig = configuration.GetSection("Scheduling")
        .Get<SchedulingConfig>() ?? new SchedulingConfig();
    services.AddSingleton(schedulingConfig);

    // Feature gates control which subsystem tools are exposed
    var featureGates = new FeatureGates(
        MemoryEnabled: memoryConfig.Enabled,
        SearchEnabled: searchConfig.Enabled,
        SkillSyncEnabled: skillSyncConfig.Enabled,
        SubAgentsEnabled: subAgentConfig.Enabled,
        SchedulingEnabled: schedulingConfig.Enabled);
    var fileApprovalMatcher = new FilePathApprovalMatcher(paths.ConfigDirectory);
    var shellTrustZonePolicy = new ShellTrustZonePolicy(toolConfig, paths);
    // Safe-verbs list: bundled per-OS defaults only — embedded resource in
    // Netclaw.Configuration with no on-disk user override. Used by the
    // approval gate's verb-pattern Layer to auto-allow demonstrably
    // read-only verbs when invoked inside a trusted zone. Widening the
    // list goes through code review and a daemon release, not a config
    // edit, so the agent has no path to extend its own auto-pass surface
    // at runtime.
    var safeVerbs = SafeVerbLoader.Load(shellEnvironment.Platform == ShellPlatform.Windows);
    services.AddSingleton(safeVerbs);

    var toolAccessPolicy = new ToolAccessPolicy(
        toolConfig,
        effectivePolicyDefaults,
        shellCommandPolicy,
        toolPathPolicy,
        fileApprovalMatcher,
        featureGates,
        shellTrustZonePolicy,
        safeVerbs);
    services.AddSingleton(toolAccessPolicy);

    var approvalShell = shellEnvironment.Grammar switch
    {
        ShellGrammar.Bash => ApprovalShell.Bash,
        ShellGrammar.PowerShell => ApprovalShell.PowerShell,
        _ => throw new InvalidOperationException("The native shell grammar is invalid.")
    };
    var toolApprovalStore = new ToolApprovalStore(
        paths.ToolApprovalsPath,
        TimeProvider.System,
        new ApprovalStoreMigrationContext(approvalShell));
    services.AddSingleton(toolApprovalStore);
    services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();

    var toolRegistry = new ToolRegistry();
    toolRegistry.WithFirstPartyTools(toolConfig, paths, toolPathPolicy, shellCommandPolicy, searchBackend, toolAccessPolicy,
        webhooksConfig.Enabled ? webhookRouteStore : null);

    // Skills system: seed built-in skills to .system/, register sync service
    CopyBuiltInSkills(paths.SystemSkillsDirectory);
    var skillRegistry = new SkillRegistry();

    // External skill sources (Claude Code, Open Code, custom paths)
    var externalSkillsConfig = configuration.GetSection("ExternalSkills")
        .Get<ExternalSkillsConfig>() ?? new ExternalSkillsConfig();
    var resolvedExternalSources = externalSkillsConfig.ResolveEnabledSources();
    services.AddSingleton(externalSkillsConfig);
    services.AddSingleton(resolvedExternalSources);

    // Server feed skill sources (private skill-server instances)
    var skillFeedsConfig = configuration.GetSection("SkillFeeds")
        .Get<SkillFeedsConfig>() ?? new SkillFeedsConfig();
    services.AddSingleton(skillFeedsConfig);

    services.AddSingleton(skillRegistry);

    // Subagent definition registry and file loader
    var subAgentRegistry = new SubAgentDefinitionRegistry();
    services.AddSingleton(subAgentRegistry);
    services.AddSingleton<FileSubAgentDefinitionLoader>();
    services.AddSingleton<SubAgentSpawner>();

    // New SQLite-backed memory substrate (uses existing daemon SQLite file by design)
    // Store is always created for schema migration; memory services are gated on MemoryConfig.Enabled.
    var memoryStore = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
    services.AddSingleton(memoryStore);

    // Schema migration hosted service must start before any memory consumer so
    // both akka-persistence migrations and memory table creation run first.
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<SchemaMigrationHostedService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SchemaMigrationHostedService>());

    if (memoryConfig.Enabled)
    {
        services.AddSingleton<IMemoryRecallCoordinator, SQLiteMemoryRecallCoordinator>();
        services.AddSingleton<MemoryPolicyEvaluator>();
        services.AddSingleton<MemoryRulesFirstExtractor>();
        services.AddSingleton<MemoryCurationEngine>();
        services.AddSingleton<IMemoryCheckpointSink, SQLiteMemoryCheckpointSink>();
        services.AddSingleton<MemoryCurationWorkerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MemoryCurationWorkerService>());

        // SQLite-first mode: explicit manual-control memory tools are always routed
        // through the SQLite memory + checkpoint/policy pipeline.
        toolRegistry.Register(new SqliteFindMemoriesTool(memoryStore));
        toolRegistry.Register(new SqliteGetMemoriesTool(memoryStore));
        toolRegistry.Register(new SqliteStoreMemoryTool(new SQLiteMemoryCheckpointSink(memoryStore, TimeProvider.System)));
        toolRegistry.Register(new SqliteUpdateMemoryTool(memoryStore));

        // Embedding foundation (memory-core-redesign Slice 2). The holder always exists —
        // starts pointed at an Unavailable stub so any consumer resolving it before warmup
        // completes gets a safe, explicit degraded value rather than a null reference — and
        // EmbeddingWarmupHostedService populates it at startup (see that type's remarks for why
        // a mutable holder is required instead of constructor injection).
        services.AddHttpClient("EmbeddingModelProvisioner").AddNetclawHeaders("embedding-provisioner");
        services.AddSingleton<IReadOnlyDictionary<string, EmbeddingModelManifestEntry>>(
            EmbeddingModelProvisioner.Allowlist);
        services.AddSingleton(sp => new EmbeddingModelProvisioner(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("EmbeddingModelProvisioner"),
            EmbeddingModelProvisioner.Allowlist));

        // Initial prefix/floor are resolved from the allowlist entry (memory-query-prefix design
        // D2/D3) rather than hardcoded empty/null placeholders: an unknown ModelId degrades to
        // "no prefix, no calibration" here (TryGetValue returns null) exactly like any other
        // missing-manifest-entry condition elsewhere — the daemon still starts, and
        // EmbeddingWarmupHostedService's own load attempt is what surfaces the loud failure.
        EmbeddingModelProvisioner.Allowlist.TryGetValue(memoryConfig.Embeddings.ModelId, out var initialEmbeddingEntry);
        services.AddSingleton(_ => new MemoryEmbedderHolder(
            new UnavailableMemoryEmbedder(memoryConfig.Embeddings.ModelId, "embedding warmup has not completed yet"),
            initialQueryPrefix: initialEmbeddingEntry?.QueryPrefix ?? string.Empty,
            initialCalibratedMinCosineSimilarity: initialEmbeddingEntry?.CalibratedMinCosineSimilarity));

        // Vector index for the curation evaluator's embedding kNN nominator (memory-core-
        // redesign Slice 3 Stage B, task 3.1). Registered alongside MemoryEmbedderHolder above:
        // both are optional dependencies of MemoryCurationActor/MemoryCurationEngine that
        // degrade to the lexical content-term search when either is absent or the embedder is
        // unavailable.
        services.AddSingleton(new MemoryVectorIndexHolder(memoryStore));

        // Post-floor relevance gate (memory-relevance-gate D4). Same holder-and-warmup pattern
        // as MemoryEmbedderHolder above; also an optional dependency of
        // SQLiteMemoryRecallCoordinator, which degrades to floor-only behavior when this holder's
        // current scorer is unavailable.
        services.AddSingleton<IReadOnlyDictionary<string, RelevanceModelManifestEntry>>(
            EmbeddingModelProvisioner.RelevanceAllowlist);
        services.AddSingleton(_ => new RelevanceScorerHolder(
            new UnavailableRelevanceScorer(
                EmbeddingModelProvisioner.DefaultRelevanceModelId, "relevance gate warmup has not completed yet"),
            initialCalibratedThreshold: EmbeddingModelProvisioner.RelevanceAllowlist[EmbeddingModelProvisioner.DefaultRelevanceModelId].CalibratedThreshold));

        services.AddSingleton<EmbeddingWarmupHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<EmbeddingWarmupHostedService>());
    }

    services.AddSingleton<IMemoryExtractor>(NullMemoryExtractor.Instance);

    services.AddSingleton(toolRegistry);
    services.AddSingleton<IToolExecutor>(sp =>
        new DispatchingToolExecutor(
            toolRegistry,
            toolAccessPolicy,
            sp.GetService<IToolApprovalService>(),
            sp.GetRequiredService<ILogger<DispatchingToolExecutor>>()));
    // Operational notification webhooks
    var notificationsConfig = configuration.GetSection("Notifications")
        .Get<NotificationsConfig>() ?? new NotificationsConfig();
    services.AddSingleton(notificationsConfig);

    if (notificationsConfig.Webhooks.Count > 0)
    {
        services.AddHttpClient("Notifications").AddNetclawHeaders("webhook");
        services.AddSingleton<WebhookNotificationService>();
        services.AddSingleton<IOperationalNotificationSink>(sp =>
            sp.GetRequiredService<WebhookNotificationService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<WebhookNotificationService>());
    }
    else
    {
        services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);
    }

    // Posts reminder failure notices to the reminder's destination channel.
    // IChannelRegistry is always registered (AddChannelRegistry below), so the
    // real notifier is always available; it no-ops gracefully for reminders whose
    // channel has no outbound client.
    services.AddSingleton<Netclaw.Actors.Reminders.IReminderChannelNotifier,
        Netclaw.Daemon.Reminders.ReminderChannelFailureNotifier>();

    // Daemon lifecycle notifier (startup/shutdown webhooks + logging)
    services.AddSingleton<DaemonLifecycleNotifier>();

    // MCP server lifecycle management
    var mcpServers = configuration.GetSection("McpServers")
        .Get<Dictionary<string, McpServerEntry>>() ?? [];
    services.AddSingleton(mcpServers);
    services.AddHttpClient("ProviderOAuth").AddNetclawHeaders("provider-oauth");
    services.AddSingleton(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ProviderOAuth");
        return new OAuthPkceService(httpClient);
    });
    services.AddSingleton<IProviderOAuthCallbackListener, ProviderOAuthCallbackListener>();
    services.AddSingleton(sp => new McpOAuthCredentialStore(
        paths,
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ISecretsProtector>(),
        sp.GetRequiredService<ILogger<McpOAuthCredentialStore>>()));
    services.AddHttpClient("McpOAuth").AddNetclawHeaders("mcp-oauth");
    services.AddSingleton(sp => new McpOAuthClientRegistrar(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("McpOAuth"),
        sp.GetRequiredService<ILogger<McpOAuthClientRegistrar>>()));
    services.AddSingleton(sp => new McpOAuthFlowBroker(
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
    services.AddSingleton<IMcpClientRuntime, McpClientRuntime>();
    services.AddSingleton<McpClientManager>();
    services.AddSingleton<IMcpPromptSkillLoader>(sp => sp.GetRequiredService<McpClientManager>());
    services.AddHostedService(sp => sp.GetRequiredService<McpClientManager>());
    services.AddSingleton<IMcpReconnectable>(sp => sp.GetRequiredService<McpClientManager>());
    services.AddHostedService<McpReconnectionService>();

    // Dynamic tool index context layer — NOT part of the persisted system prompt.
    // The prompt-facing layer is computed from the live registry with audience
    // filtering so startup context matches actual discoverable capabilities.
    // Shadow files remain on disk for operator inspection across daemon restarts.
    services.AddSingleton<McpShadowCatalogWriter>();
    services.AddSingleton<ToolIndexContextLayer>();
    services.AddSingleton<IContextLayerProvider>(sp => sp.GetRequiredService<ToolIndexContextLayer>());

    // Skill index context layer — origin-free logical catalog rebuilt with the complete inventory.
    var skillIndexLayer = new SkillIndexContextLayer(skillSyncConfig);
    services.AddSingleton(skillIndexLayer);
    services.AddSingleton<IContextLayerProvider>(skillIndexLayer);
    var skillIndexPublisher = new SkillIndexPublisher(skillRegistry, skillIndexLayer, toolAccessPolicy);
    services.AddSingleton(skillIndexPublisher);
    var skillInventoryRefresher = new SkillInventoryRefresher(
        paths, skillFeedsConfig, resolvedExternalSources, skillRegistry, skillIndexPublisher);
    var initialSkillScan = skillInventoryRefresher.Refresh();
    services.AddSingleton(skillInventoryRefresher);

    // Skill tools are registered post-build so ISkillContentScanner resolves from DI.
    // See SkillToolRegistration call after app.Build().
    if (initialSkillScan.Issues.Count > 0)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(daemonLogLevel));
        var startupLogger = loggerFactory.CreateLogger("Netclaw.Startup");
        startupLogger.LogWarning(
            "Skill inventory is degraded at startup: accepted={AcceptedSkillCount} rejected={RejectedIssueCount}",
            initialSkillScan.AcceptedSkills.Count,
            initialSkillScan.Issues.Count);

        foreach (var issue in initialSkillScan.Issues)
        {
            startupLogger.LogWarning(
                "Rejected skill item during startup scan: kind={IssueKind} path={Path} message={Message}",
                issue.Kind,
                issue.Path,
                issue.Message);
        }
    }

    // Skill tools are registered post-build so ISkillContentScanner resolves from DI.
    // See SkillToolRegistration call after app.Build().

    // Memory context layer — status is updated by ToolIndexUpdater after MCP discovery
    var memoryIndexLayer = new MemoryIndexContextLayer(memoryConfig);
    services.AddSingleton(memoryIndexLayer);
    services.AddSingleton<IContextLayerProvider>(memoryIndexLayer);

    // Subagent discovery context layer — rebuilds the catalog on demand from the live file snapshot.
    services.AddSingleton(sp => new SubAgentDiscoveryContextLayer(
        subAgentConfig,
        sp.GetRequiredService<SubAgentDefinitionRegistry>(),
        sp.GetRequiredService<FileSubAgentDefinitionLoader>(),
        paths));
    services.AddSingleton<IContextLayerProvider>(sp => sp.GetRequiredService<SubAgentDiscoveryContextLayer>());

    // Current time context layer — transient per-turn grounding for date/time-sensitive prompts
    services.AddSingleton<IContextLayerProvider, CurrentTimeContextLayer>();
    services.AddSingleton<IGitWorkingContextInspector, GitWorkingContextInspector>();
    services.AddSingleton<IWorkingContextSnapshotProvider, WorkingContextSnapshotProvider>();

    // Expose all context layers as IReadOnlyList for actor DI resolution
    services.AddSingleton<IReadOnlyList<IContextLayerProvider>>(sp =>
        sp.GetServices<IContextLayerProvider>().ToList());
    services.AddHostedService<ToolIndexUpdater>();

    // System skills feed sync — checks CDN for updated skills at startup.
    // Runs after initial skill scan; re-scans and updates the index if any skills changed.
    // Also enriches skills with keyword indexes for deterministic auto-loading.
    // Never blocks startup on network failures.
    // Gated on SkillSyncConfig.Enabled — when disabled, no CDN sync occurs.
    if (skillSyncConfig.Enabled)
    {
        services.AddHttpClient<SystemSkillSyncService>(client =>
            client.Timeout = FeedConstants.FeedHttpTimeout).AddNetclawHeaders("skill-sync");
        services.AddHostedService<SystemSkillSyncService>();
    }

    // Server feed sync — syncs skills from private skill-server instances at startup.
    // Runs after SystemSkillSyncService; each feed syncs independently.
    if (skillFeedsConfig.Feeds.Any(f => f.Enabled))
    {
        services.AddHostedService<ServerFeedSkillSyncService>();
    }

    // Skill directory watcher — auto-rescan when skill files change on disk.
    // Covers native skills directory, server feeds, and all external sources.
    // Registered after sync services so initial sync completes first.
    services.AddSingleton<SkillDirectoryWatcherService>();
    services.AddHostedService(sp => sp.GetRequiredService<SkillDirectoryWatcherService>());

    // Binary update check — logs a warning at startup if a newer version is available.
    // Never blocks startup, never downloads anything.
    // Result is cached in UpdateCheckService for 1 hour; DaemonRuntimeStatusService
    // reads it via the static cache when building the status API response.
    services.AddHttpClient<BinaryUpdateCheckService>(client =>
        client.Timeout = FeedConstants.BinaryFeedHttpTimeout).AddNetclawHeaders("update-check");
    services.AddHostedService<BinaryUpdateCheckService>();

    // System prompt (file-based, with first-run seed)
    // Seed minimal SOUL.md if neither new nor legacy personality file exists
    if (!File.Exists(paths.SoulPath) && !File.Exists(paths.PersonalityPath))
        File.WriteAllText(paths.SoulPath,
            "# You are Netclaw\n\nBe concise and direct. Act autonomously — use your tools "
            + "to do things rather than telling the user how.\n");
    var promptProvider = new FileSystemPromptProvider(paths);
    services.AddSingleton<ISystemPromptProvider>(promptProvider);

    var sqlitePath = string.IsNullOrWhiteSpace(persistence.Sqlite.Path)
        ? paths.SqliteDbPath
        : persistence.Sqlite.Path!;

    // Model capability resolution chain:
    // [Ollama →] [OpenAI-compat →] OpenRouter oracle → HuggingFace → text-only default.
    // When the main provider is Ollama, query it first — it knows the true context window
    // for locally hosted models that may not be indexed by external oracles.
    services.AddHttpClient<OpenRouterOracleResolver>().AddNetclawHeaders("capability-probe");
    services.AddHttpClient<HuggingFaceCapabilityResolver>().AddNetclawHeaders("capability-probe");
    if (ollamaEndpoint is not null)
    {
        services.AddHttpClient(nameof(OllamaCapabilityResolver)).AddNetclawHeaders("capability-probe");
        services.AddSingleton(sp =>
            new OllamaCapabilityResolver(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaCapabilityResolver)),
                sp.GetRequiredService<ILogger<OllamaCapabilityResolver>>(),
                ollamaEndpoint));
    }
    if (openAiCompatibleEndpoint is not null)
    {
        services.AddHttpClient(nameof(OpenAiCompatibleCapabilityResolver)).AddNetclawHeaders("capability-probe");
        services.AddSingleton(sp =>
            new OpenAiCompatibleCapabilityResolver(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenAiCompatibleCapabilityResolver)),
                sp.GetRequiredService<ILogger<OpenAiCompatibleCapabilityResolver>>(),
                openAiCompatibleEndpoint,
                openAiCompatibleApiKey));
    }
    // modelId → provider type lookup for CompositeCapabilityResolver scoping.
    // Covers Main + optional Compaction independently so multi-provider
    // deployments resolve each model's capabilities against the right backend.
    var modelProviderLookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(models.Main.ModelId))
        modelProviderLookup[models.Main.ModelId] = mainProviderType;
    if (models.Compaction is { ModelId: { Length: > 0 } compactionId } &&
        providers.TryGetValue(models.Compaction.Provider, out var compactionProvider))
        modelProviderLookup[compactionId] = compactionProvider.Type;

    services.AddSingleton<IModelCapabilityResolver>(sp =>
    {
        var resolvers = new List<IModelCapabilityResolver>();
        if (ollamaEndpoint is not null)
            resolvers.Add(sp.GetRequiredService<OllamaCapabilityResolver>());
        if (openAiCompatibleEndpoint is not null)
            resolvers.Add(sp.GetRequiredService<OpenAiCompatibleCapabilityResolver>());
        resolvers.Add(sp.GetRequiredService<OpenRouterOracleResolver>());
        resolvers.Add(sp.GetRequiredService<HuggingFaceCapabilityResolver>());

        return new CompositeCapabilityResolver(
            resolvers,
            sp.GetRequiredService<ILogger<CompositeCapabilityResolver>>(),
            modelId => modelProviderLookup.TryGetValue(modelId, out var pt) ? pt : null);
    });

    // Composite dependency records for LlmSessionActor DI resolution
    services.AddSingleton(sp => new SessionServices(
        sp.GetRequiredService<IChatClientProvider>(),
        sp.GetRequiredService<ISystemPromptProvider>(),
        sp.GetRequiredService<IReadOnlyList<IContextLayerProvider>>(),
        sp.GetRequiredService<IWorkingContextSnapshotProvider>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<NetclawPaths>()));

    services.AddSingleton(sp => new SessionToolServices(
        sp.GetRequiredService<IToolExecutor>(),
        sp.GetRequiredService<ToolRegistry>(),
        sp.GetService<ToolAccessPolicy>(),
        sp.GetService<TrustContextDeriver>(),
        sp.GetService<SkillRegistry>(),
        sp.GetService<IToolApprovalService>(),
        sp.GetService<SubAgentDefinitionRegistry>(),
        sp.GetService<SubAgentSpawner>(),
        sp.GetService<FileSubAgentDefinitionLoader>()));

    services.AddSingleton(sp => new SessionMemoryServices(
        sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
        sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
        sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
        sp.GetService<SQLiteMemoryStore>(),
        sp.GetService<MemoryConfig>(),
        sp.GetService<MemoryEmbedderHolder>(),
        sp.GetService<MemoryVectorIndexHolder>()));

    services.AddSingleton(sp => new SessionObservability(
        sp.GetService<Netclaw.Actors.Telemetry.ISessionMetrics>(),
        sp.GetService<ISessionLifecycleObserver>()));

    // Akka.NET actor system
    services.AddAkka("netclaw", (akkaBuilder, sp) =>
    {
        // Prevent coordinated shutdown from calling Environment.Exit(),
        // which would kill the process before the restart loop can iterate.
        // The before-service-unbind phase needs a generous timeout (DaemonConfig.
        // GracefulShutdownBudget) because sessions mid-LLM-call (TurnLlmTimeout defaults to
        // 3 minutes) must finish before passivation can begin. See DaemonConfig.
        // GracefulShutdownBudget remarks for the full set of surfaces this must stay in
        // lockstep with.
        akkaBuilder.AddHocon(
            DaemonShutdownConfiguration.BuildCoordinatedShutdownHocon(DaemonConfig.GracefulShutdownBudget),
            HoconAddMode.Prepend);

        akkaBuilder = akkaBuilder.ConfigureLoggers(setup =>
        {
            setup.ClearLoggers();
            setup.AddLoggerFactory();
            setup.LogLevel = ToAkkaLogLevel(daemonLogLevel);
        });

        if (persistence.Provider is PersistenceProvider.Sqlite)
        {
            var connectionString = $"Data Source={sqlitePath}";
            akkaBuilder = akkaBuilder.WithSqlPersistence(
                connectionString: connectionString,
                providerName: "SQLite.MS");
        }
        else
        {
            akkaBuilder = akkaBuilder
                .WithInMemoryJournal()
                .WithInMemorySnapshotStore();
        }

        var reminderStorage = persistence.Provider is PersistenceProvider.Sqlite
            ? new NetclawAkkaHostingExtensions.ReminderStorageOptions
            {
                SqliteConnectionString = $"Data Source={sqlitePath}",
                TableName = "netclaw_reminders",
                AutoInitialize = true
            }
            : null;

        akkaBuilder.WithNetclawSerialization();
        akkaBuilder.WithNetclawActors(shellEnvironment, reminderStorage);
        akkaBuilder.WithWebhookRouteActor();
        akkaBuilder.WithSessionLogDispatcher(paths.SessionLogsDirectory, sp.GetRequiredService<TimeProvider>());
        akkaBuilder.WithSignalRGateway();
        akkaBuilder.WithDailyStatsActor();

        // Register reminder tools after actors start (needs ReminderManagerActor ref)
        akkaBuilder.StartActors((system, registry, _) =>
        {
            var reminderManager = registry.Get<Netclaw.Actors.Hosting.ReminderManagerActorKey>();
            var tp = sp.GetRequiredService<TimeProvider>();
            var historyStore = sp.GetRequiredService<ReminderHistoryStore>();
            var targetResolvers = sp.GetServices<Netclaw.Actors.Reminders.IReminderTargetResolver>();
            var schedulingCfg = sp.GetRequiredService<SchedulingConfig>();
            toolRegistry.WithReminderTools(reminderManager, tp, historyStore, schedulingCfg, targetResolvers);

            var bgJobManager = registry.Get<Netclaw.Actors.Hosting.BackgroundJobManagerActorKey>();
            toolRegistry.WithBackgroundJobTools(bgJobManager);

            // Route mutation tools ask the webhook route actor, so they register
            // here rather than with the other first-party tools. The webhooks
            // config gate matches the one on `list_webhooks` above.
            if (webhooksConfig.Enabled)
                toolRegistry.WithWebhookRouteTools(registry.Get<Netclaw.Actors.Hosting.WebhookRouteActorKey>());

            // Drain all active LLM sessions during any actor system termination (SIGTERM, daemon stop).
            // Runs in an early CoordinatedShutdown phase while actors are still alive.
            // If DaemonRestartCoordinator already drained sessions (config reload), the ingress
            // gate will be closed and this task skips its drain to avoid double-draining.
            // The phase timeout (DaemonConfig.GracefulShutdownBudget) is generous because
            // sessions mid-LLM-call must finish before passivation can begin.
            var cs = CoordinatedShutdown.Get(system);
            var sessionManager = registry.Get<SessionManagerActorKey>();
            var ingressGate = sp.GetRequiredService<SessionIngressGate>();
            var lifecycleNotifier = sp.GetRequiredService<DaemonLifecycleNotifier>();
            var drainLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Netclaw.Daemon.SessionDrain");

            cs.AddTask(CoordinatedShutdown.PhaseBeforeServiceUnbind, "drain-llm-sessions", async () =>
            {
                if (!ingressGate.TryClose("Daemon shutting down."))
                {
                    drainLogger.LogDebug("Ingress gate already closed; skipping CoordinatedShutdown session drain.");
                    return Akka.Done.Instance;
                }

                try
                {
                    // Bounded strictly under DaemonConfig.GracefulShutdownBudget (the Akka phase
                    // timeout above) so the drain always completes -- timed out or not -- before
                    // the phase timeout itself fires and abandons this task outright.
                    // netclaw-dev/netclaw#1664: a session parked on interactive tool approval
                    // never acks PrepareForDaemonRestart, so an unbounded wait here (previously
                    // CancellationToken.None, CancellationToken.None) hung for the full 200s
                    // phase timeout with no timeout of its own, leaking the abandoned drain task.
                    using var drainDeadlineCts = new CancellationTokenSource(DaemonConfig.BoundedDrainTimeout, tp);

                    var drainResult = await SessionDrainHelper.DrainAsync(
                        sessionManager,
                        "daemon-stop",
                        drainLogger,
                        drainDeadlineCts.Token,
                        CancellationToken.None);

                    lifecycleNotifier.NotifyShutdown("daemon-stop", drainResult.ToNotificationContext());
                }
                catch (Exception ex)
                {
                    drainLogger.LogWarning(ex, "Session drain during shutdown failed; sessions will recover from last durable checkpoint.");
                }

                return Akka.Done.Instance;
            });
        });
    });

    // Content security (magic-byte file scanning + prompt-injection detector)
    services.AddContentSecurity();

    // Session pipeline (stream API for channels)
    services.AddSingleton<SessionPipeline>();
    services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());

    services.AddChannelRegistry();
    services.AddTuiChannelDescriptor();
    // Also registers the generic channel tools (send_channel_message + the two
    // lookups) whenever at least one remote chat channel is enabled.
    services.AddChannelIntegrations(configuration);

    // Config hot-reload watcher
    services.AddSingleton<ConfigWatcherService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConfigWatcherService>());

    // Warm previously active sessions after a coordinated restart.
    services.AddSingleton<RestartRecoveryService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RestartRecoveryService>());

    // PID file authority for daemon lifecycle management
    services.AddSingleton<PidFileService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PidFileService>());

    // PID file watchdog — self-terminates daemon if PID file is deleted externally.
    // Registered after PidFileService so it starts after the PID file is written
    // and stops before PidFileService deletes it during graceful shutdown.
    services.AddSingleton<IHostedService, PidFileWatchdogService>();

    // Active session cleanup during host shutdown
    services.AddSingleton<SessionRegistryShutdownService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SessionRegistryShutdownService>());
}

static ISearchBackend? CreateSearchBackend(SearchConfig config)
{
    switch (config.Backend)
    {
        case SearchBackend.Brave:
            if (config.BraveApiKey.IsNullOrEmpty())
            {
                Console.Error.WriteLine("warn: Brave Search configured but no API key provided (Search.BraveApiKey). Web search tool will not be registered.");
                return null;
            }
            return new BraveSearchBackend(config.BraveApiKey.Value);

        case SearchBackend.SearXng:
            if (string.IsNullOrWhiteSpace(config.SearXngEndpoint))
            {
                Console.Error.WriteLine("warn: SearXNG configured but no endpoint provided (Search.SearXngEndpoint). Web search tool will not be registered.");
                return null;
            }
            return new SearXngBackend(config.SearXngEndpoint);

        case SearchBackend.DuckDuckGo:
            return new DuckDuckGoBackend();

        default:
            throw new ArgumentOutOfRangeException(nameof(config.Backend), config.Backend,
                $"Unknown search backend: {config.Backend}");
    }
}

/// <summary>
/// Copies built-in system skills from the daemon's embedded resources into
/// build output as <c>BuiltInSkills/{skill-name}/SKILL.md</c> (with companion files).
/// Only writes files that do not already exist (feed updates are preserved).
/// </summary>
static void CopyBuiltInSkills(string skillsDirectory)
{
    var builtInDir = Path.Combine(AppContext.BaseDirectory, "BuiltInSkills");
    if (!Directory.Exists(builtInDir))
        return;

    foreach (var sourceFile in Directory.EnumerateFiles(builtInDir, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(builtInDir, sourceFile);
        var targetPath = Path.Combine(skillsDirectory, relativePath);

        if (File.Exists(targetPath))
            continue;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourceFile, targetPath);
    }
}

static Akka.Event.LogLevel ToAkkaLogLevel(LogLevel logLevel)
{
    return logLevel switch
    {
        Trace or Debug => Akka.Event.LogLevel.DebugLevel,
        Information => Akka.Event.LogLevel.InfoLevel,
        Warning => Akka.Event.LogLevel.WarningLevel,
        Error or Critical or None => Akka.Event.LogLevel.ErrorLevel,
        _ => Akka.Event.LogLevel.WarningLevel
    };
}

public partial class Program;
