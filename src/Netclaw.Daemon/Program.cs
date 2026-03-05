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
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Configuration.Secrets;
using Netclaw.Configuration.Feeds;
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

    var app = builder.Build();

    // Gateway surface
    app.MapHub<SessionHub>("/hub/session");
    app.MapGet("/api/health/ready", () => Results.Ok("healthy"));
    app.MapGet("/api/health/status", async (DaemonRuntimeStatusService statusService, CancellationToken cancellationToken) =>
        Results.Ok(await statusService.GetStatusAsync(cancellationToken)));
    app.MapGet("/api/sessions", (SessionCatalogService catalog) =>
        Results.Ok(catalog.ListRecent(limit: 50)));

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

    // Register channel-specific tools after DI is built (tools need resolved services).
    ChannelToolRegistration.RegisterChannelTools(app.Services);

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

    services.AddLlmProviders(providers, models);

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
            ? Netclaw.Configuration.Providers.Descriptors.OllamaDescriptor.DefaultEndpointValue
            : mainProvider.Endpoint)
        : null;

    var inputModalities = models.Main.InputModalities;
    var outputModalities = models.Main.OutputModalities;
    int? contextWindow = models.Main.ContextWindow;
    if (inputModalities is null || outputModalities is null || contextWindow is null)
    {
        var detected = ResolveStartupCapabilities(
            models.Main.ModelId, daemonLogLevel, mainProviderType, ollamaEndpoint);
        if (detected is not null)
        {
            inputModalities ??= detected.InputModalities;
            outputModalities ??= detected.OutputModalities;
            contextWindow ??= detected.ContextWindowTokens;
        }
    }

    // Session config: bind defaults from config section, overlay model-derived values
    var sessionConfig = configuration.GetSection("Session").Get<SessionConfig>() ?? new SessionConfig();
    var resolvedSessionConfig = sessionConfig with
    {
        ModelId = models.Main.ModelId,
        ContextWindowTokens = contextWindow ?? 32_768,
        CompactionModelId = models.Compaction?.ModelId,
        InputModalities = inputModalities ?? ModelModality.Text,
        OutputModalities = outputModalities ?? ModelModality.Text,
    };
    services.AddSingleton(resolvedSessionConfig);

    // Tools (auto-bound, no required properties)
    var toolConfig = configuration.GetSection("Tools")
        .Get<ToolConfig>() ?? new ToolConfig();
    services.AddSingleton(toolConfig);

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

    // Cross-session memory: provider-based wiring
    var memoryConfig = configuration.GetSection("Memory")
        .Get<MemoryConfig>() ?? new MemoryConfig();
    services.AddSingleton(memoryConfig);

    if (memoryConfig.Provider.Equals("memorizer", StringComparison.OrdinalIgnoreCase))
    {
        // Memorizer path: subagent-backed memory tools (store_memory, search_memories) are
        // registered later by ToolIndexUpdater after MCP discovery completes and Memorizer
        // connectivity is confirmed. The compaction extractor still uses direct MCP delegation.
        services.AddSingleton<IMemoryExtractor>(sp =>
            new MemorizerMemoryExtractor(sp.GetRequiredService<ToolRegistry>()));
    }
    else
    {
        // File path: register builtin memory tools backed by local markdown files
        var fileStore = new FileMemoryStore(paths.MemoriesDirectory, TimeProvider.System);
        services.AddSingleton(fileStore);
        toolRegistry.Register(new FileFindMemoriesTool(fileStore));
        toolRegistry.Register(new FileGetMemoriesTool(fileStore));
        toolRegistry.Register(new StoreMemoryTool(fileStore));
        toolRegistry.Register(new FileUpdateMemoryTool(fileStore));
        services.AddSingleton<IMemoryExtractor>(new FileMemoryExtractor(fileStore));
    }

    services.AddSingleton(toolRegistry);
    services.AddSingleton<IToolExecutor>(sp =>
        new DispatchingToolExecutor(toolRegistry, sp.GetRequiredService<ILogger<DispatchingToolExecutor>>()));

    // MCP server lifecycle management
    var mcpServers = configuration.GetSection("McpServers")
        .Get<Dictionary<string, McpServerEntry>>() ?? new();
    services.AddSingleton(mcpServers);
    services.AddHttpClient<McpOAuthService>();
    services.AddSingleton<McpOAuthService>();
    services.AddSingleton<McpClientManager>();
    services.AddHostedService(sp => sp.GetRequiredService<McpClientManager>());

    // Dynamic tool index context layer — NOT part of the persisted system prompt.
    // Backed by system-managed shadow files on disk so tool metadata remains
    // discoverable and inspectable across daemon restarts.
    services.AddSingleton<McpShadowCatalogWriter>();
    services.AddSingleton<IContextLayerProvider>(_ =>
        new FileContextLayerProvider(paths.ToolIndexShadowPath));

    // Skill index context layer
    var skillIndexLayer = new SkillIndexContextLayer();
    skillIndexLayer.Update(skillRegistry.GenerateCompressedIndex());
    services.AddSingleton(skillIndexLayer);
    services.AddSingleton<IContextLayerProvider>(skillIndexLayer);

    // Memory context layer — status is updated by ToolIndexUpdater after MCP discovery
    var memoryIndexLayer = new MemoryIndexContextLayer();
    services.AddSingleton(memoryIndexLayer);
    services.AddSingleton<IContextLayerProvider>(memoryIndexLayer);

    // Expose all context layers as IReadOnlyList for actor DI resolution
    services.AddSingleton<IReadOnlyList<IContextLayerProvider>>(sp =>
        sp.GetServices<IContextLayerProvider>().ToList());
    services.AddHostedService<ToolIndexUpdater>();

    // System skills feed sync — checks CDN for updated skills at startup.
    // Runs after initial skill scan; re-scans and updates the index if any skills changed.
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

    // Model capability resolution chain: [Ollama →] OpenRouter oracle → HuggingFace → text-only default
    // When the main provider is Ollama, query it first — it knows the true context window
    // for locally hosted models that may not be indexed by external oracles.
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
    services.AddSingleton<IModelCapabilityResolver>(sp =>
    {
        var resolvers = new List<IModelCapabilityResolver>();
        if (ollamaEndpoint is not null)
            resolvers.Add(sp.GetRequiredService<OllamaCapabilityResolver>());
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

        akkaBuilder.WithNetclawActors();
    });

    // Content security (magic-byte file scanning + prompt-injection detector)
    services.AddContentSecurity();

    // Session pipeline (stream API for channels)
    services.AddSingleton<SessionPipeline>();

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
    string modelId, LogLevel logLevel, string? providerType, string? ollamaEndpoint)
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

        // Fallback: OpenRouter public catalog (works for models from any provider)
        var openRouterDescriptor = new Netclaw.Configuration.Providers.Descriptors.OpenRouterDescriptor(httpClient);
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
/// Copies built-in skill files from embedded resources to the skills directory.
/// Only writes a file if it does not already exist (user edits are preserved).
/// </summary>
static void CopyBuiltInSkills(string skillsDirectory)
{
    var assembly = typeof(Program).Assembly;
    const string prefix = "Netclaw.Daemon.BuiltInSkills.";

    foreach (var resourceName in assembly.GetManifestResourceNames())
    {
        if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
            continue;

        var fileName = resourceName[prefix.Length..];
        var targetPath = Path.Combine(skillsDirectory, fileName);

        if (File.Exists(targetPath))
            continue;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            continue;

        using var fileStream = File.Create(targetPath);
        stream.CopyTo(fileStream);
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
