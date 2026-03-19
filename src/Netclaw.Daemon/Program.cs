using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Akka.Persistence.Sql.Hosting;
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
using Netclaw.Daemon.Services;
using Netclaw.Search;
using Netclaw.Security;
using static Microsoft.Extensions.Logging.LogLevel;

try
{
    var restartSignal = new DaemonRestartSignal();
    do
    {
        restartSignal.Reset();
        await RunDaemonAsync(args, restartSignal);
    } while (restartSignal.RestartRequested);
}
catch (Exception ex)
{
    WriteCrashLog(ex);
    throw;
}

static async Task RunDaemonAsync(string[] args, DaemonRestartSignal restartSignal)
{
    // Anchor process CWD to a user-owned temp directory.
    // Without this, the daemon runs from its install location (e.g. /usr/local/bin),
    // which means shell commands, relative file paths, and stdio MCP child processes
    // (Playwright screenshots, etc.) all default to a potentially privileged directory.
    var netclawTempDir = Path.Combine(Path.GetTempPath(), "netclaw");
    Directory.CreateDirectory(netclawTempDir);
    Environment.CurrentDirectory = netclawTempDir;

    var builder = WebApplication.CreateBuilder(args);

    // Use port 5199 to avoid conflicts with Aspire (5000) and other defaults
    builder.WebHost.UseUrls("http://127.0.0.1:5199");

    // Register process-lifetime restart signal so services can trigger a restart
    builder.Services.AddSingleton(restartSignal);

    var paths = ConfigureConfigServices(builder.Services, builder.Configuration);
    var daemonLogLevel = builder.ConfigureNetclawLogging();
    builder.AddNetclawTelemetry();
    ConfigureDaemonServices(builder.Services, builder.Configuration, paths, daemonLogLevel);

    // SignalR for remote clients (CLI thin client, Blazor ops console)
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<SessionCatalogService>();
    builder.Services.AddSingleton<ISessionLifecycleObserver>(sp => sp.GetRequiredService<SessionCatalogService>());
    builder.Services.AddSingleton<SessionRegistry>();
    builder.Services.AddSingleton<DaemonRuntimeStatusService>();
    builder.Services.AddSingleton<DailyStatsPublisher>();
    builder.Services.AddSingleton<Netclaw.Actors.Telemetry.ISessionMetrics>(sp => sp.GetRequiredService<DailyStatsPublisher>());
    builder.Services.AddSingleton<DaemonStatsService>();

    var app = builder.Build();

    // Gateway surface
    app.MapHub<SessionHub>("/hub/session");
    app.MapGet("/api/health/ready", () => Results.Ok("healthy"));
    app.MapGet("/api/health/status", async (DaemonRuntimeStatusService statusService, CancellationToken cancellationToken) =>
        Results.Ok(await statusService.GetStatusAsync(cancellationToken)));
    app.MapGet("/api/sessions", (SessionCatalogService catalog) =>
        Results.Ok(catalog.ListRecent(limit: 50)));
    app.MapGet("/api/stats", async (DaemonStatsService statsService, int? days, CancellationToken ct) =>
        Results.Ok(await statsService.GetStatsAsync(days, ct)));

    // MCP OAuth 2.1 endpoints
    app.MapPost("/api/mcp/oauth/start/{name}", async (
        string name,
        McpOAuthService oauthService,
        Dictionary<string, McpServerEntry> mcpServers,
        CancellationToken ct) =>
    {
        if (!mcpServers.TryGetValue(name, out var entry))
            return Results.NotFound(new { error = $"MCP server '{name}' not found" });

        if (string.IsNullOrWhiteSpace(entry.Url))
            return Results.BadRequest(new { error = $"MCP server '{name}' has no URL (OAuth requires HTTP transport)" });

        var (authUrl, state) = await oauthService.StartAuthorizationFlowAsync(name, entry, ct);
        return Results.Ok(new { authorizationUrl = authUrl, state });
    });

    app.MapGet("/api/mcp/oauth/callback", async (
        HttpContext context,
        McpOAuthService oauthService,
        CancellationToken ct) =>
    {
        var code = context.Request.Query["code"].ToString();
        var state = context.Request.Query["state"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body><h2>Authorization failed</h2><p>Missing code or state parameter.</p></body></html>", ct);
            return;
        }

        try
        {
            await oauthService.CompleteAuthorizationAsync(code, state, ct);
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body><h2>Authorization complete</h2><p>You may close this tab.</p></body></html>", ct);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>", ct);
        }
    });

    app.MapGet("/api/mcp/oauth/status/{name}", (string name, McpOAuthService oauthService) =>
    {
        var status = oauthService.GetFlowStatus(name);
        return Results.Ok(new { status = status.ToString() });
    });

    // Provider OAuth endpoints (browser-based Authorization Code + PKCE)
    app.MapPost("/api/provider/oauth/start", (
        HttpContext context,
        OAuthPkceService pkceService,
        ProviderDescriptorRegistry registry) =>
    {
        var providerType = context.Request.Query["provider"].ToString();
        if (string.IsNullOrEmpty(providerType))
            return Results.BadRequest(new { error = "Missing 'provider' query parameter" });

        if (!registry.TryGet(providerType, out var descriptor))
            return Results.NotFound(new { error = $"Unknown provider type: {providerType}" });

        var oauth = descriptor.Auth.GetOAuthConfig();
        if (oauth is null || oauth.AuthorizationEndpoint is null || oauth.RedirectUri is null)
            return Results.BadRequest(new { error = $"Provider '{providerType}' does not support browser OAuth" });

        var (authUrl, state) = pkceService.StartAuthorizationFlow(
            oauth.AuthorizationEndpoint.AbsoluteUri,
            oauth.TokenEndpoint.AbsoluteUri,
            oauth.ClientId,
            oauth.RedirectUri.AbsoluteUri,
            oauth.Scope,
            oauth.ExtraAuthParams);

        // Start temporary callback listener on the redirect URI's port
        _ = pkceService.ListenForCallbackAsync(oauth.RedirectUri.AbsoluteUri, state);

        return Results.Ok(new { authorizationUrl = authUrl, state });
    });

    app.MapGet("/api/provider/oauth/callback", async (
        HttpContext context,
        OAuthPkceService pkceService,
        CancellationToken ct) =>
    {
        var code = context.Request.Query["code"].ToString();
        var state = context.Request.Query["state"].ToString();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body><h2>Authorization failed</h2><p>Missing code or state parameter.</p></body></html>", ct);
            return;
        }

        try
        {
            await pkceService.CompleteAuthorizationAsync(code, state, ct);
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body><h2>Authorization complete</h2><p>You may close this tab and return to the terminal.</p></body></html>", ct);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>", ct);
        }
    });

    app.MapGet("/api/provider/oauth/status/{state}", (
        string state,
        OAuthPkceService pkceService) =>
    {
        var status = pkceService.GetFlowStatus(state);
        var result = pkceService.GetFlowResult(state);
        return Results.Ok(new
        {
            status = status.ToString(),
            hasToken = result is not null,
            accessToken = result?.AccessToken.Value,
            refreshToken = result?.RefreshToken?.Value,
            expiresAt = result?.ExpiresAt?.ToString("o"),
        });
    });

    // Register channel-specific tools after DI is built (tools need resolved services).
    ChannelToolRegistration.RegisterChannelTools(app.Services);

    // Reminder REST API
    MapReminderEndpoints(app);

    await app.RunAsync();
}

static void WriteCrashLog(Exception ex)
{
    CrashLogWriter.Write(ex, "daemon");
}

// ═══════════════════════════════════════════════════════════════════════
// Shared configuration services
// ═══════════════════════════════════════════════════════════════════════

static NetclawPaths ConfigureConfigServices(IServiceCollection services, IConfigurationManager configuration)
{
    // Netclaw paths (creates ~/.netclaw/ structure)
    var paths = new NetclawPaths();
    paths.EnsureDirectoriesExist();
    services.AddSingleton(paths);

    // Initialize Data Protection for secrets encryption/decryption.
    // Must happen before config binding so SensitiveStringTypeConverter
    // can transparently decrypt ENC: values.
    var protector = SecretsProtection.CreateProtector(paths);
    services.AddSingleton<ISecretsProtector>(protector);
    SensitiveStringTypeConverter.Protector = protector;

    // Layered configuration chain:
    // 1. netclaw.json (base config, optional)
    // 2. secrets.json (credentials overlay, optional)
    // 3. NETCLAW_* environment variables (highest priority)
    configuration
        .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
        .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("NETCLAW_");

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
    LogLevel daemonLogLevel)
{
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
        options.ShutdownTimeout = TimeSpan.FromSeconds(10);
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

    var (inputModalities, outputModalities, contextWindow) =
        ModelCapabilityResolution.ResolveSessionConfig(models.Main, detected);

    // Session config: bind defaults from config section, overlay model-derived values
    var sessionConfig = configuration.GetSection("Session").Get<SessionConfig>() ?? new SessionConfig();
    var resolvedSessionConfig = sessionConfig with
    {
        ModelId = models.Main.ModelId,
        ContextWindowTokens = contextWindow,
        CompactionModelId = models.Compaction?.ModelId,
        InputModalities = inputModalities,
        OutputModalities = outputModalities,
    };
    services.AddSingleton(resolvedSessionConfig);

    // Tools (auto-bound, no required properties)
    var toolConfig = configuration.GetSection("Tools")
        .Get<ToolConfig>() ?? new ToolConfig();
    services.AddSingleton(toolConfig);

    // Reminders
    var reminderConfig = configuration.GetSection("Reminders")
        .Get<ReminderConfig>() ?? new ReminderConfig();
    services.AddSingleton(reminderConfig);
    services.AddSingleton<ReminderDefinitionStore>();
    services.AddSingleton<ReminderHistoryStore>();

    // Search backend selection
    var searchConfig = configuration.GetSection("Search")
        .Get<SearchConfig>() ?? new SearchConfig();
    var searchBackend = CreateSearchBackend(searchConfig);

    // Tool path deny-list: prevent agent tools from accessing secrets
    var toolPathPolicy = new ToolPathPolicy([paths.SecretsPath, paths.KeysDirectory]);
    services.AddSingleton(toolPathPolicy);

    var toolRegistry = new ToolRegistry();
    toolRegistry.WithFirstPartyTools(toolConfig, searchBackend, toolPathPolicy);

    // Skills system: seed built-in skills to .system/, register sync service
    CopyBuiltInSkills(paths.SystemSkillsDirectory);
    var skillRegistry = new SkillRegistry();
    foreach (var skill in SkillScanner.Scan(paths.SkillsDirectory))
        skillRegistry.Register(skill);
    services.AddSingleton(skillRegistry);

    // Subagent timeout configuration
    var subAgentConfig = configuration.GetSection("SubAgents")
        .Get<SubAgentConfig>() ?? new SubAgentConfig();
    services.AddSingleton(subAgentConfig);

    // Subagent definition registry and file loader
    var subAgentRegistry = new SubAgentDefinitionRegistry();
    services.AddSingleton(subAgentRegistry);
    services.AddSingleton<FileSubAgentDefinitionLoader>();
    services.AddSingleton<SubAgentSpawner>();

    // Cross-session memory: provider-based wiring
    var memoryConfig = configuration.GetSection("Memory")
        .Get<MemoryConfig>() ?? new MemoryConfig();
    services.AddSingleton(memoryConfig);

    // System skill sync behavior
    var skillSyncConfig = configuration.GetSection("SkillSync")
        .Get<SkillSyncConfig>() ?? new SkillSyncConfig();
    services.AddSingleton(skillSyncConfig);

    // New SQLite-backed memory substrate (uses existing daemon SQLite file by design)
    var memoryStore = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
    services.AddSingleton(memoryStore);
    services.AddSingleton<IMemoryRecallCoordinator, SQLiteMemoryRecallCoordinator>();
    services.AddSingleton<MemoryPolicyEvaluator>();
    services.AddSingleton<MemoryRulesFirstExtractor>();
    services.AddSingleton<MemoryCurationEngine>();
    services.AddSingleton<IMemoryCheckpointSink, SQLiteMemoryCheckpointSink>();
    services.AddSingleton<MemoryCurationWorkerService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MemoryCurationWorkerService>());

    // Ensure memory schema exists on startup (idempotent)
    memoryStore.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

    // SQLite-first mode: explicit manual-control memory tools are always routed
    // through the SQLite memory + checkpoint/policy pipeline.
    toolRegistry.Register(new SqliteFindMemoriesTool(memoryStore));
    toolRegistry.Register(new SqliteGetMemoriesTool(memoryStore));
    toolRegistry.Register(new SqliteStoreMemoryTool(new SQLiteMemoryCheckpointSink(memoryStore, TimeProvider.System)));
    toolRegistry.Register(new SqliteUpdateMemoryTool(
        memoryStore,
        new SQLiteMemoryCheckpointSink(memoryStore, TimeProvider.System)));
    services.AddSingleton<IMemoryExtractor>(NullMemoryExtractor.Instance);

    services.AddSingleton(toolRegistry);
    services.AddSingleton<IToolExecutor>(sp =>
        new DispatchingToolExecutor(toolRegistry, sp.GetRequiredService<ILogger<DispatchingToolExecutor>>()));

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

    // MCP server lifecycle management
    var mcpServers = configuration.GetSection("McpServers")
        .Get<Dictionary<string, McpServerEntry>>() ?? new();
    services.AddSingleton(mcpServers);
    services.AddHttpClient<McpOAuthService>();
    services.AddSingleton<McpOAuthService>();
    services.AddSingleton(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ProviderOAuth");
        return new OAuthPkceService(httpClient);
    });
    services.AddHttpClient("ProviderOAuth");
    services.AddSingleton<McpClientManager>();
    services.AddHostedService(sp => sp.GetRequiredService<McpClientManager>());

    // Dynamic tool index context layer — NOT part of the persisted system prompt.
    // Backed by system-managed shadow files on disk so tool metadata remains
    // discoverable and inspectable across daemon restarts.
    services.AddSingleton<McpShadowCatalogWriter>();
    services.AddSingleton<IContextLayerProvider>(_ =>
        new FileContextLayerProvider(paths.ToolIndexShadowPath, ContextLayerTiming.OnceAtStart));

    // Skill index context layer
    var skillIndexLayer = new SkillIndexContextLayer();
    skillIndexLayer.Update(skillRegistry.GenerateCompressedIndex());
    services.AddSingleton(skillIndexLayer);
    services.AddSingleton<IContextLayerProvider>(skillIndexLayer);

    // Memory context layer — status is updated by ToolIndexUpdater after MCP discovery
    var memoryIndexLayer = new MemoryIndexContextLayer();
    services.AddSingleton(memoryIndexLayer);
    services.AddSingleton<IContextLayerProvider>(memoryIndexLayer);

    // Subagent discovery context layer — updated by ToolIndexUpdater after file-based agents load
    var subAgentDiscoveryLayer = new SubAgentDiscoveryContextLayer();
    services.AddSingleton(subAgentDiscoveryLayer);
    services.AddSingleton<IContextLayerProvider>(subAgentDiscoveryLayer);

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
    services.AddHttpClient<SystemSkillSyncService>(client =>
        client.Timeout = FeedConstants.FeedHttpTimeout);
    services.AddHostedService<SystemSkillSyncService>();

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
            "You are Netclaw, a helpful homelab operations assistant. "
            + "Be concise and direct. Act autonomously — use your tools to do things "
            + "rather than telling the user how.");
    var promptProvider = new FileSystemPromptProvider(paths);
    services.AddSingleton<ISystemPromptProvider>(promptProvider);

    var sqlitePath = string.IsNullOrWhiteSpace(persistence.Sqlite.Path)
        ? paths.SqliteDbPath
        : persistence.Sqlite.Path!;

    // Schema migration for SQLite persistence
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<SchemaMigrationHostedService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SchemaMigrationHostedService>());

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
    services.AddSingleton<IModelCapabilityResolver>(sp =>
    {
        var resolvers = new List<IModelCapabilityResolver>();
        resolvers.Add(sp.GetRequiredService<OpenAiCodexCapabilityResolver>());
        if (ollamaEndpoint is not null)
            resolvers.Add(sp.GetRequiredService<OllamaCapabilityResolver>());
        if (openAiCompatibleEndpoint is not null)
            resolvers.Add(sp.GetRequiredService<OpenAiCompatibleCapabilityResolver>());
        resolvers.Add(sp.GetRequiredService<OpenRouterOracleResolver>());
        resolvers.Add(sp.GetRequiredService<HuggingFaceCapabilityResolver>());

        return new CompositeCapabilityResolver(
            resolvers,
            sp.GetRequiredService<ILogger<CompositeCapabilityResolver>>());
    });

    // Akka.NET actor system
    services.AddAkka("netclaw", (akkaBuilder, sp) =>
    {
        // Prevent coordinated shutdown from calling Environment.Exit(),
        // which would kill the process before the restart loop can iterate.
        akkaBuilder.AddHocon(
            "akka.coordinated-shutdown.exit-clr = off",
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

        akkaBuilder.WithNetclawActors(reminderStorage);
        akkaBuilder.WithSignalRGateway();
        akkaBuilder.WithDailyStatsActor();

        // Register reminder tools after actors start (needs ReminderManagerActor ref)
        akkaBuilder.StartActors((system, registry, _) =>
        {
            var reminderManager = registry.Get<Netclaw.Actors.Hosting.ReminderManagerActorKey>();
            var tp = sp.GetRequiredService<TimeProvider>();
            var rc = sp.GetRequiredService<ReminderConfig>();
            var historyStore = sp.GetRequiredService<ReminderHistoryStore>();
            toolRegistry.WithReminderTools(reminderManager, tp, rc, historyStore);
        });
    });

    // Content security (magic-byte file scanning + prompt-injection detector)
    services.AddContentSecurity();

    // Session pipeline (stream API for channels)
    services.AddSingleton<SessionPipeline>();
    services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());

    services.AddSlackChannelIntegration(configuration);

    // Config hot-reload watcher
    services.AddSingleton<ConfigWatcherService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConfigWatcherService>());

    // PID file authority for daemon lifecycle management
    services.AddSingleton<PidFileService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PidFileService>());

    // Active session cleanup during host shutdown
    services.AddSingleton<SessionRegistryShutdownService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SessionRegistryShutdownService>());
}

static ISearchBackend? CreateSearchBackend(SearchConfig config)
{
    var backend = config.Backend.ToLowerInvariant();
    switch (backend)
    {
        case "brave":
            if (config.BraveApiKey is null || string.IsNullOrWhiteSpace(config.BraveApiKey.Value))
            {
                Console.Error.WriteLine("warn: Brave Search configured but no API key provided (Search.BraveApiKey). Web search tool will not be registered.");
                return null;
            }
            return new BraveSearchBackend(config.BraveApiKey.Value);

        case "searxng":
            if (string.IsNullOrWhiteSpace(config.SearXngEndpoint))
            {
                Console.Error.WriteLine("warn: SearXNG configured but no endpoint provided (Search.SearXngEndpoint). Web search tool will not be registered.");
                return null;
            }
            return new SearXngBackend(config.SearXngEndpoint);

        case "duckduckgo":
            return new DuckDuckGoBackend();

        default:
            Console.Error.WriteLine($"warn: Unknown search backend '{backend}'. Falling back to DuckDuckGo.");
            return new DuckDuckGoBackend();
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
                    modelId, ollamaResult.InputModalities, ollamaResult.OutputModalities,
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
                    modelId, openAiCompatibleResult.InputModalities, openAiCompatibleResult.OutputModalities,
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
                modelId, result.InputModalities, result.OutputModalities,
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
/// Copies built-in skill files from the output directory to the skills directory.
/// Skills are sourced from <c>feeds/skills/.system/files/</c> and copied to the
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

// ═══════════════════════════════════════════════════════════════════════
// Reminder REST API
// ═══════════════════════════════════════════════════════════════════════

static void MapReminderEndpoints(WebApplication app)
{
    app.MapGet("/api/reminders", async (
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<Netclaw.Actors.Reminders.ReminderListResponse>(
            new Netclaw.Actors.Reminders.ListRemindersCommand(), TimeSpan.FromSeconds(10), ct);
        var projected = response.Reminders.Select(r => new
        {
            id = r.Id.Value,
            title = r.Title,
            enabled = r.Enabled,
            schedule = Netclaw.Actors.Reminders.ListRemindersTool.DescribeSchedule(r.Schedule),
            nextFire = Netclaw.Actors.Reminders.SetReminderTool.FormatNextFire(r.NextFire),
        });
        return Results.Ok(projected);
    });

    app.MapPost("/api/reminders", async (
        CreateReminderRequest request,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ReminderConfig reminderConfig,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);

        string? reportToChannel = request.ReportToChannel;
        string? notifyInstructions = request.NotifyInstructions;

        if (!string.IsNullOrWhiteSpace(request.ReportTarget))
        {
            var resolver = serviceProvider.GetService<Netclaw.Channels.Slack.ISlackTargetResolver>();
            if (resolver is null)
                return Results.BadRequest(new { error = "Slack is not enabled; cannot resolve report target." });

            var resolved = await resolver.ResolveAsync(request.ReportTarget, ct);
            if (!resolved.Success)
                return Results.BadRequest(new { error = resolved.ErrorMessage ?? "Failed to resolve report target." });

            if (!string.IsNullOrWhiteSpace(resolved.UserId))
            {
                var targetUserId = resolved.UserId;
                reportToChannel = null;
                notifyInstructions = $"Send a direct message to Slack user {targetUserId} with your findings, or lack thereof.";
            }
            else
            {
                reportToChannel = resolved.ChannelId;
                if (string.IsNullOrWhiteSpace(notifyInstructions))
                    notifyInstructions = $"Post the result to Slack channel {reportToChannel}.";
            }
        }

        // Use caller-provided ID if available, otherwise auto-generate for backward compatibility
        var effectiveId = !string.IsNullOrWhiteSpace(request.Id)
            ? request.Id
            : Netclaw.Actors.Reminders.ReminderIdGenerator.Generate(request.Name).Value;

        var tool = new Netclaw.Actors.Reminders.SetReminderTool(manager, timeProvider, reminderConfig);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = effectiveId,
                ["Name"] = request.Name,
                ["Prompt"] = request.Prompt,
                ["ScheduleType"] = request.ScheduleType,
                ["Schedule"] = request.Schedule,
                ["ReportToChannel"] = reportToChannel,
                ["NotifyInstructions"] = notifyInstructions
            }, ct);

        return result.StartsWith("Error", StringComparison.Ordinal)
            ? Results.BadRequest(new { error = result })
            : Results.Ok(new { message = result });
    });

    app.MapPost("/api/reminders/validate", (
        CreateReminderRequest request,
        TimeProvider timeProvider,
        ReminderConfig reminderConfig) =>
    {
        var (schedule, error) = ReminderScheduleParser.Parse(
            request.ScheduleType,
            request.Schedule,
            timeProvider,
            reminderConfig);

        if (schedule is null)
            return Results.BadRequest(new { valid = false, error });

        return Results.Ok(new { valid = true, scheduleType = schedule.Type.ToString(), nextFire = schedule.FireAt });
    });

    app.MapPost("/api/reminders/import", async (
        ImportReminderRequest request,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        if (request.Definition is null)
            return Results.BadRequest(new { error = "Reminder definition is required." });

        var mode = request.WriteMode?.Trim().ToLowerInvariant() switch
        {
            "replace" => ReminderWriteMode.Replace,
            "upsert" => ReminderWriteMode.Upsert,
            null or "" or "create" or "createonly" => ReminderWriteMode.CreateOnly,
            _ => (ReminderWriteMode?)null
        };

        if (mode is null)
            return Results.BadRequest(new { error = "Invalid writeMode. Use create, replace, or upsert." });

        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(request.Definition, mode.Value),
            TimeSpan.FromSeconds(10),
            ct);

        if (!response.Success)
        {
            var status = response.Error is ReminderSaveError.Conflict
                ? StatusCodes.Status409Conflict
                : response.Error is ReminderSaveError.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;

            return Results.Json(new
            {
                error = response.ErrorMessage ?? "Import failed.",
                code = response.Error.ToString(),
                id = response.Id.Value
            }, statusCode: status);
        }

        return Results.Ok(new
        {
            id = response.Id.Value,
            title = response.Title,
            nextFire = response.NextFire,
            message = $"Imported reminder '{response.Id.Value}'."
        });
    });

    app.MapDelete("/api/reminders/{id}", async (
        string id,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<Netclaw.Actors.Reminders.ReminderCancelledResponse>(
            new Netclaw.Actors.Reminders.CancelReminderCommand(new Netclaw.Actors.Reminders.ReminderId(id)),
            TimeSpan.FromSeconds(10), ct);

        return response.Found
            ? Results.Ok(new { message = $"Reminder '{id}' cancelled." })
            : Results.NotFound(new { error = $"Reminder '{id}' not found." });
    });

    app.MapPost("/api/reminders/{id}/disable", async (
        string id,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<ReminderStateResponse>(
            new DisableReminderCommand(new ReminderId(id)),
            TimeSpan.FromSeconds(10),
            ct);

        return !response.Found
            ? Results.NotFound(new { error = response.ErrorMessage ?? $"Reminder '{id}' not found." })
            : Results.Ok(new { id = id, enabled = response.Enabled, message = $"Reminder '{id}' disabled." });
    });

    app.MapPost("/api/reminders/{id}/enable", async (
        string id,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<ReminderStateResponse>(
            new EnableReminderCommand(new ReminderId(id)),
            TimeSpan.FromSeconds(10),
            ct);

        if (!response.Found)
            return Results.NotFound(new { error = response.ErrorMessage ?? $"Reminder '{id}' not found." });
        if (!response.Enabled && !string.IsNullOrWhiteSpace(response.ErrorMessage))
            return Results.BadRequest(new { error = response.ErrorMessage, id, enabled = false });

        return Results.Ok(new { id, enabled = response.Enabled, nextFire = response.NextFire, message = $"Reminder '{id}' enabled." });
    });

    app.MapGet("/api/reminders/{id}", async (
        string id,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<Netclaw.Actors.Reminders.GetReminderResponse>(
            new Netclaw.Actors.Reminders.GetReminderCommand(new Netclaw.Actors.Reminders.ReminderId(id)),
            TimeSpan.FromSeconds(10), ct);

        if (response.Reminder is null)
            return Results.NotFound(new { error = $"Reminder '{id}' not found." });

        var r = response.Reminder;
        return Results.Ok(new
        {
            id = r.Id.Value,
            title = r.Title,
            enabled = r.Enabled,
            schedule = Netclaw.Actors.Reminders.ListRemindersTool.DescribeSchedule(r.Schedule),
            nextFire = Netclaw.Actors.Reminders.SetReminderTool.FormatNextFire(r.NextFire),
            instructions = r.Instructions,
            notifyInstructions = r.NotifyInstructions,
            sessionId = r.SessionId,
            reportToChannel = r.ReportToChannel,
        });
    });

    app.MapGet("/api/reminders/{id}/history", async (
        string id,
        int? last,
        ReminderDefinitionStore definitionStore,
        ReminderHistoryStore historyStore,
        CancellationToken ct) =>
    {
        var rid = new ReminderId(id);
        if (!definitionStore.Exists(rid))
            return Results.NotFound(new { error = $"Reminder '{id}' not found." });

        var maxRecords = Math.Clamp(last ?? 20, 1, 500);
        var records = await historyStore.ReadAsync(rid, maxRecords);
        return Results.Ok(records);
    });
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

/// <summary>
/// REST API request body for creating a reminder.
/// </summary>
sealed record CreateReminderRequest
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required string ScheduleType { get; init; }
    public required string Schedule { get; init; }
    public string? ReportToChannel { get; init; }
    public string? ReportTarget { get; init; }
    public string? NotifyInstructions { get; init; }
}

sealed record ImportReminderRequest
{
    public required ReminderDefinition Definition { get; init; }
    public string? WriteMode { get; init; }
}
