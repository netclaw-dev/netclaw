// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.RateLimiting;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Akka.Persistence.Sql.Hosting;
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
using Netclaw.Configuration;
using Netclaw.Providers;
using ShellSyntaxTree;
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
using Netclaw.Daemon.Webhooks;
using Netclaw.Search;
using Netclaw.Tools;
using Netclaw.Security;
using static Microsoft.Extensions.Logging.LogLevel;

var bootstrapPaths = new NetclawPaths();
bootstrapPaths.EnsureDirectoriesExist();
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
            await RunDaemonAsync(args, restartSignal, crashMonitor);
        } while (restartSignal.RestartRequested);
    }
}
catch (Exception ex)
{
    crashMonitor.RecordTopLevelException(ex);
    throw;
}

static async Task RunDaemonAsync(string[] args, DaemonRestartSignal restartSignal, DaemonCrashMonitor crashMonitor)
{
    // Anchor process CWD to a user-owned temp directory.
    // Without this, the daemon runs from its install location (e.g. /usr/local/bin),
    // which means shell commands, relative file paths, and stdio MCP child processes
    // (Playwright screenshots, etc.) all default to a potentially privileged directory.
    var netclawTempDir = Path.Combine(Path.GetTempPath(), "netclaw");
    Directory.CreateDirectory(netclawTempDir);
    Environment.CurrentDirectory = netclawTempDir;

    var builder = WebApplication.CreateBuilder(args);

    // Register process-lifetime restart signal so services can trigger a restart
    builder.Services.AddSingleton(restartSignal);

    // Load configuration first (netclaw.json, secrets.json, env vars) so that
    // DaemonConfig.Host/Port can be read before binding the WebHost URL.
    var paths = ConfigureConfigServices(builder.Services, builder.Configuration);

    // Bind listen address from DaemonConfig; falls back to 127.0.0.1:5199 if
    // the Daemon section is absent from netclaw.json.
    var daemonConfig = DaemonConfig.BindFromConfiguration(builder.Configuration.GetSection("Daemon"));
    builder.WebHost.UseUrls($"http://{daemonConfig.Host}:{daemonConfig.Port}");
    var daemonLogLevel = builder.ConfigureNetclawLogging(paths);
    builder.AddNetclawTelemetry();
    ConfigureDaemonServices(builder.Services, builder.Configuration, paths, daemonLogLevel, daemonConfig);

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

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    // Gateway surface
    app.MapHub<SessionHub>("/hub/session");
    app.MapGet("/api/health/ready", () => Results.Ok("healthy"));
    app.MapGet("/api/health/status", async (DaemonRuntimeStatusService statusService, CancellationToken cancellationToken) =>
        Results.Ok(await statusService.GetStatusAsync(cancellationToken))).RequireAuthorization();
    app.MapGet("/api/sessions", (SessionCatalogService catalog) =>
        Results.Ok(catalog.ListRecent(limit: 50))).RequireAuthorization();
    app.MapGet("/api/stats", async (DaemonStatsService statsService, int? days, CancellationToken ct) =>
        Results.Ok(await statsService.GetStatsAsync(days, ct))).RequireAuthorization();
    app.MapGet("/api/stats/skills", async (DaemonStatsService statsService, int? days, CancellationToken ct) =>
        Results.Ok(await statsService.GetSkillUsageStatsAsync(days, ct))).RequireAuthorization();
    app.MapWebhookEndpoints();

    app.MapPairingEndpoints();

    app.MapMcpEndpoints();

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

static NetclawPaths ConfigureConfigServices(IServiceCollection services, IConfigurationManager configuration)
{
    // Bootstrap paths with defaults to locate config files.
    var bootstrapPaths = new NetclawPaths();

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
    var paths = new NetclawPaths(workspacesDirectory: workspacesDir);
    paths.EnsureDirectoriesExist();
    services.AddSingleton(paths);

    // TimeProvider (virtualized for testing)
    services.AddSingleton(TimeProvider.System);

    // Providers and model resolution via plugin architecture
    var providers = configuration.GetSection("Providers")
        .Get<Dictionary<string, ProviderEntry>>()
        ?? new() { ["local-ollama"] = new ProviderEntry() };
    var models = configuration.GetSection("Models")
        .Get<ModelSelection>() ?? new ModelSelection();

    services.AddDaemonLlmProviders(providers, models);

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
    DaemonConfig daemonConfig)
{
    // Daemon bind address and exposure mode (computed once in RunDaemonAsync)
    services.AddSingleton(daemonConfig);

    // Validate tunnel prerequisites before the rest of the daemon starts.
    // Throws from StartAsync to abort startup if the required process is missing.
    services.AddHostedService<ExposureModeValidationService>();
    services.AddHostedService<BootstrapCompletionMarkerService>();

    services
        .AddOptions<ModelSelection>()
        .Bind(configuration.GetSection("Models"))
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
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    // Resolve models for session config
    var models = configuration.GetSection("Models")
        .Get<ModelSelection>() ?? new ModelSelection();
    services.AddSingleton(models);

    // Auto-detect model capabilities when not manually specified in config.
    // Provider-first resolution: query the hosting provider (e.g. Ollama /api/show)
    // before falling back to external oracles (OpenRouter, HuggingFace).
    var providers = configuration.GetSection("Providers")
        .Get<Dictionary<string, ProviderEntry>>()
        ?? new() { ["local-ollama"] = new ProviderEntry() };
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

    var detected = ResolveStartupCapabilities(
        models.Main.ModelId, daemonLogLevel, mainProviderType, ollamaEndpoint, openAiCompatibleEndpoint, openAiCompatibleApiKey);

    var modelCapabilities = ModelCapabilityResolution.ResolveModelCapabilities(models, detected);
    services.AddSingleton(modelCapabilities);

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

    // Reminders — no config surface exposed. Settings live as private
    // consts on ReminderManagerActor / ReminderExecutionActor /
    // ReminderScheduleParser / ReminderHistoryStore. Library defaults
    // cover AckTimeout, MaxRetryBackoff, and MaxDeliveryAttempts.
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

    // ConfigDirectory is hard-denied for agent writes and shell access to
    // close the prompt-injection vector where an injected payload would
    // instruct the agent to rewrite tool-approvals.json, hard-deny-overrides.json,
    // or netclaw.json and grant itself global trust. Operators retain agency
    // by editing config files outside the agent (their own editor) or via
    // dedicated CLI commands that bypass the agent's tool-call path.
    // Individual high-sensitivity paths (Secrets, Keys, etc.) are also listed
    // explicitly for self-documenting intent — ConfigDirectory subsumes them
    // but the explicit entries make the security purpose obvious to readers.
    var writeDenyList = new[]
    {
        paths.ConfigDirectory,
        paths.SecretsPath,
        paths.KeysDirectory,
        paths.SqliteDbPath,
        paths.PidFilePath,
        paths.LockFilePath,
        paths.RestartManifestPath,
    };
    var readDenyList = new[]
    {
        paths.SecretsPath,
        paths.KeysDirectory,
        paths.WebhooksDirectory,
    };
    var shellIndicatorList = new[]
    {
        paths.ConfigDirectory,
        paths.SecretsPath,
        paths.WebhooksDirectory,
        paths.KeysDirectory,
        paths.SqliteDbPath,
        paths.PidFilePath,
        paths.LockFilePath,
        paths.RestartManifestPath,
    };
    var toolPathPolicy = new ToolPathPolicy(writeDenyList, readDenyList, shellIndicatorList);
    services.AddSingleton(toolPathPolicy);

    // Load operator-authored hard-deny overrides (additive only — see
    // HardDenyOverridesLoader). Missing file → empty list and only shipped
    // defaults apply. Malformed file → daemon refuses to start; the
    // loader throws InvalidDataException with operator-facing context so
    // the failure surfaces loudly rather than silently dropping rules.
    var hardDenyOverridesLoader = new HardDenyOverridesLoader();
    var hardDenyOverrides = hardDenyOverridesLoader.Load(paths.HardDenyOverridesPath);
    services.AddSingleton(hardDenyOverridesLoader);

    var shellCommandPolicy = new ShellCommandPolicy(toolConfig.HardDenyPatterns, hardDenyOverrides);
    services.AddSingleton(shellCommandPolicy);

    services.AddShellParser();

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
    var safeVerbs = SafeVerbLoader.Load();
    services.AddSingleton(safeVerbs);

    var bashParser = new BashParser();
    services.AddSingleton<IShellParser>(bashParser);

    var toolAccessPolicy = new ToolAccessPolicy(
        toolConfig,
        effectivePolicyDefaults,
        shellCommandPolicy,
        fileApprovalMatcher,
        toolPathPolicy,
        featureGates,
        shellTrustZonePolicy,
        safeVerbs);
    services.AddSingleton(toolAccessPolicy);

    var toolApprovalStore = new ToolApprovalStore(paths.ToolApprovalsPath);
    services.AddSingleton(toolApprovalStore);
    services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();

    var toolRegistry = new ToolRegistry();
    toolRegistry.WithFirstPartyTools(toolConfig, searchBackend, toolPathPolicy, shellCommandPolicy, toolAccessPolicy, paths,
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

    var resolvedServerFeedSources = new List<ResolvedExternalSource>();
    foreach (var feed in skillFeedsConfig.Feeds.Where(f => f.Enabled))
    {
        var feedDir = paths.ServerFeedDirectory(feed.Name);
        if (Directory.Exists(feedDir))
            resolvedServerFeedSources.Add(new ResolvedExternalSource(
                $"server-feed:{feed.Name}", [feedDir], AllowSymlinks: false));
    }
    IReadOnlyList<ResolvedExternalSource> serverFeeds = resolvedServerFeedSources;
    services.AddKeyedSingleton("server-feeds", serverFeeds);

    // Scan native skills first (highest precedence), then server feeds, then external sources
    var initialSkillScan = SkillScanner.ScanAndMerge(
        paths.SkillsDirectory, serverFeeds, resolvedExternalSources);
    skillRegistry.ReplaceAll(initialSkillScan.AcceptedSkills, initialSkillScan.Issues);
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
        toolRegistry.Register(new SqliteUpdateMemoryTool(
            memoryStore,
            new SQLiteMemoryCheckpointSink(memoryStore, TimeProvider.System)));
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
        services.AddHttpClient("Notifications");
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

    // Daemon lifecycle notifier (startup/shutdown webhooks + logging)
    services.AddSingleton<DaemonLifecycleNotifier>();

    // MCP server lifecycle management
    var mcpServers = configuration.GetSection("McpServers")
        .Get<Dictionary<string, McpServerEntry>>() ?? [];
    services.AddSingleton(mcpServers);
    services.AddHttpClient("ProviderOAuth");
    services.AddSingleton(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ProviderOAuth");
        return new OAuthPkceService(httpClient);
    });
    services.AddSingleton<IProviderOAuthCallbackListener, ProviderOAuthCallbackListener>();
    services.AddHttpClient(nameof(McpOAuthService));
    services.AddSingleton(sp => new McpOAuthService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(McpOAuthService)),
        paths,
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<McpOAuthService>>(),
        sp.GetRequiredService<OAuthPkceService>(),
        sp.GetRequiredService<IOperationalNotificationSink>(),
        sp.GetService<ISecretsProtector>()));
    services.AddSingleton<McpClientManager>();
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

    // Skill index context layer — compressed format pointing at files on disk, rebuilt by sync service
    var skillIndexLayer = new SkillIndexContextLayer(skillSyncConfig);
    skillIndexLayer.Update(skillRegistry.GenerateIndex(paths.SkillsDirectory, resolvedExternalSources));
    services.AddSingleton(skillIndexLayer);
    services.AddSingleton<IContextLayerProvider>(skillIndexLayer);

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
            client.Timeout = FeedConstants.FeedHttpTimeout);
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
        client.Timeout = FeedConstants.BinaryFeedHttpTimeout);
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
    // Codex static catalog → [Ollama →] [OpenAI-compat →] OpenRouter oracle → HuggingFace → text-only default
    // Codex resolver is first: authoritative for Codex models, zero network cost.
    // When the main provider is Ollama, query it next — it knows the true context window
    // for locally hosted models that may not be indexed by external oracles.
    services.AddSingleton<OpenAiCodexCapabilityResolver>();
    services.AddHttpClient<OpenRouterOracleResolver>();
    services.AddHttpClient<HuggingFaceCapabilityResolver>();
    if (ollamaEndpoint is not null)
    {
        services.AddHttpClient(nameof(OllamaCapabilityResolver));
        services.AddSingleton(sp =>
            new OllamaCapabilityResolver(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaCapabilityResolver)),
                sp.GetRequiredService<ILogger<OllamaCapabilityResolver>>(),
                ollamaEndpoint));
    }
    if (openAiCompatibleEndpoint is not null)
    {
        services.AddHttpClient(nameof(OpenAiCompatibleCapabilityResolver));
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
        var resolvers = new List<IModelCapabilityResolver>
        {
            sp.GetRequiredService<OpenAiCodexCapabilityResolver>()
        };
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
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<NetclawPaths>()));

    services.AddSingleton(sp => new SessionToolServices(
        sp.GetRequiredService<IToolExecutor>(),
        sp.GetService<IToolAuditLogger>(),
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
        sp.GetService<MemoryConfig>()));

    services.AddSingleton(sp => new SessionObservability(
        sp.GetService<Netclaw.Actors.Telemetry.ISessionMetrics>(),
        sp.GetService<ISessionLifecycleObserver>()));

    // Akka.NET actor system
    services.AddAkka("netclaw", (akkaBuilder, sp) =>
    {
        // Prevent coordinated shutdown from calling Environment.Exit(),
        // which would kill the process before the restart loop can iterate.
        // The before-service-unbind phase needs a generous timeout because sessions
        // mid-LLM-call (TurnLlmTimeout defaults to 3 minutes) must finish before
        // passivation can begin.
        akkaBuilder.AddHocon(
            """
            akka.coordinated-shutdown {
                exit-clr = off
                phases.before-service-unbind.timeout = 200s
            }
            """,
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
        akkaBuilder.WithNetclawActors(reminderStorage);
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

            // Drain all active LLM sessions during any actor system termination (SIGTERM, daemon stop).
            // Runs in an early CoordinatedShutdown phase while actors are still alive.
            // If DaemonRestartCoordinator already drained sessions (config reload), the ingress
            // gate will be closed and this task skips its drain to avoid double-draining.
            // The phase timeout (200s) is generous because sessions mid-LLM-call must finish
            // before passivation can begin.
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
                    var drainResult = await SessionDrainHelper.DrainAsync(
                        sessionManager, "daemon-stop", drainLogger, CancellationToken.None);

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

    services.AddSlackChannelIntegration(configuration);
    services.AddDiscordChannelIntegration(configuration);

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
/// One-time capability detection at startup. Creates temporary HTTP resources
/// to query the hosting provider (Ollama) or OpenRouter public catalog before
/// the DI container is built.
/// Returns null if detection fails (caller falls back to text-only).
/// </summary>
static ResolvedModelCapabilities? ResolveStartupCapabilities(
    string modelId, LogLevel logLevel, string? providerType, string? ollamaEndpoint, string? openAiCompatibleEndpoint, string? openAiCompatibleApiKey)
{
    try
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(logLevel));
        var logger = loggerFactory.CreateLogger("Netclaw.Startup");

        // Provider-first: try Ollama /api/show when running against an Ollama backend
        if (providerType?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true
            && ollamaEndpoint is not null)
        {
            var ollamaResolver = new OllamaCapabilityResolver(
                httpClient, loggerFactory.CreateLogger<OllamaCapabilityResolver>(), ollamaEndpoint);
            var ollamaResult = ollamaResolver.ResolveAsync(modelId, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (ollamaResult is not null)
            {
                logger.LogInformation(
                    "Auto-detected model capabilities for {ModelId}: input={Input}, output={Output}, context_window={ContextWindow}",
                    modelId,
                    ollamaResult.InputModalities?.ToString() ?? "unknown",
                    ollamaResult.OutputModalities?.ToString() ?? "unknown",
                    ollamaResult.ContextWindowTokens?.ToString() ?? "unknown");
                return ollamaResult;
            }
        }

        if (providerType?.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) == true
            && openAiCompatibleEndpoint is not null)
        {
            var openAiCompatibleResolver = new OpenAiCompatibleCapabilityResolver(
                httpClient,
                loggerFactory.CreateLogger<OpenAiCompatibleCapabilityResolver>(),
                openAiCompatibleEndpoint,
                openAiCompatibleApiKey);
            var openAiCompatibleResult = openAiCompatibleResolver.ResolveAsync(modelId, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (openAiCompatibleResult is not null)
            {
                logger.LogInformation(
                    "Auto-detected model capabilities for {ModelId}: input={Input}, output={Output}, context_window={ContextWindow}",
                    modelId,
                    openAiCompatibleResult.InputModalities?.ToString() ?? "unknown",
                    openAiCompatibleResult.OutputModalities?.ToString() ?? "unknown",
                    openAiCompatibleResult.ContextWindowTokens?.ToString() ?? "unknown");
                return openAiCompatibleResult;
            }
        }

        // Fallback: OpenRouter public catalog (works for models from any provider)
        var openRouterDescriptor = new OpenRouterDescriptor(httpClient);
        var registry = new ProviderDescriptorRegistry([openRouterDescriptor]);
        var resolver = new OpenRouterOracleResolver(
            httpClient, loggerFactory.CreateLogger<OpenRouterOracleResolver>(), registry);

        var result = resolver.ResolveAsync(modelId, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (result is not null)
        {
            logger.LogInformation(
                "Auto-detected model capabilities for {ModelId}: input={Input}, output={Output}, context_window={ContextWindow}",
                modelId,
                result.InputModalities?.ToString() ?? "unknown",
                result.OutputModalities?.ToString() ?? "unknown",
                result.ContextWindowTokens?.ToString() ?? "unknown");
        }
        else
        {
            logger.LogInformation(
                "Model {ModelId} not found in capability oracles; defaulting to text-only",
                modelId);
        }

        return result;
    }
    catch
    {
        // Startup capability detection is best-effort — don't crash the daemon
        return null;
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
