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
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Providers;
using Netclaw.Daemon.Services;
using Netclaw.Search;
using Netclaw.Security;

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

    await app.RunAsync();
}

static void WriteCrashLog(Exception ex)
{
    try
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw", "logs");
        Directory.CreateDirectory(logsDir);

        var crashPath = Path.Combine(logsDir,
            $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(crashPath,
            $"""
            Netclaw daemon crash at {DateTime.UtcNow:O}

            {ex}
            """);

        Console.Error.WriteLine($"Fatal error — crash log written to {crashPath}");
    }
    catch
    {
        Console.Error.WriteLine($"Fatal error (could not write crash log): {ex}");
    }
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
    // Uses the OpenRouter public catalog as an oracle — works for models from
    // any provider since OpenRouter indexes most publicly available models.
    var inputModalities = models.Main.InputModalities;
    var outputModalities = models.Main.OutputModalities;
    int? contextWindow = models.Main.ContextWindow;
    if (inputModalities is null || outputModalities is null || contextWindow is null)
    {
        var detected = ResolveStartupCapabilities(models.Main.ModelId, daemonLogLevel);
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

    var toolRegistry = new ToolRegistry();
    toolRegistry.WithFirstPartyTools(toolConfig, searchBackend);

    // Skills system: copy built-in skills, scan, expose via file-based index
    CopyBuiltInSkills(paths.SkillsDirectory);
    var skillRegistry = new SkillRegistry();
    foreach (var skill in SkillScanner.Scan(paths.SkillsDirectory))
        skillRegistry.Register(skill);
    services.AddSingleton(skillRegistry);

    // Cross-session memory: search_memories wraps Memorizer MCP tool
    toolRegistry.Register(new SearchMemoriesTool(toolRegistry));

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

    // System prompt (file-based, with first-run seed)
    // Seed minimal SOUL.md if neither new nor legacy personality file exists
    if (!File.Exists(paths.SoulPath) && !File.Exists(paths.PersonalityPath))
        File.WriteAllText(paths.SoulPath,
            "You are Netclaw, a helpful homelab operations assistant. "
            + "Be concise and direct.");
    var promptProvider = new FileSystemPromptProvider(paths);
    services.AddSingleton<ISystemPromptProvider>(promptProvider);

    var sqlitePath = string.IsNullOrWhiteSpace(persistence.Sqlite.Path)
        ? paths.SqliteDbPath
        : persistence.Sqlite.Path!;

    // Schema migration for SQLite persistence
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<SchemaMigrationHostedService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SchemaMigrationHostedService>());

    // Model capability resolution chain: OpenRouter oracle → HuggingFace → text-only default
    services.AddHttpClient<OpenRouterOracleResolver>();
    services.AddHttpClient<HuggingFaceCapabilityResolver>();
    services.AddSingleton<IModelCapabilityResolver>(sp =>
        new CompositeCapabilityResolver(
            [
                sp.GetRequiredService<OpenRouterOracleResolver>(),
                sp.GetRequiredService<HuggingFaceCapabilityResolver>(),
            ],
            sp.GetRequiredService<ILogger<CompositeCapabilityResolver>>()));

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

    // Content security (no-op defaults, real scanning plugged in later)
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
            if (string.IsNullOrWhiteSpace(config.BraveApiKey))
            {
                Console.Error.WriteLine("warn: Brave Search configured but no API key provided (Search.BraveApiKey). Web search tool will not be registered.");
                return null;
            }
            return new BraveSearchBackend(config.BraveApiKey);

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
/// to query the OpenRouter public catalog before the DI container is built.
/// Returns null if detection fails (caller falls back to text-only).
/// </summary>
static ResolvedModelCapabilities? ResolveStartupCapabilities(string modelId, LogLevel logLevel)
{
    try
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(logLevel));
        var logger = loggerFactory.CreateLogger("Netclaw.Startup");
        // Build a minimal registry for the resolver (pre-DI bootstrap).
        // Only the OpenRouter descriptor is needed for capability detection.
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
                "Model {ModelId} not found in OpenRouter catalog; defaulting to text-only",
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
    var prefix = "Netclaw.Daemon.BuiltInSkills.";

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
        LogLevel.Trace => Akka.Event.LogLevel.DebugLevel,
        LogLevel.Debug => Akka.Event.LogLevel.DebugLevel,
        LogLevel.Information => Akka.Event.LogLevel.InfoLevel,
        LogLevel.Warning => Akka.Event.LogLevel.WarningLevel,
        LogLevel.Error => Akka.Event.LogLevel.ErrorLevel,
        LogLevel.Critical => Akka.Event.LogLevel.ErrorLevel,
        LogLevel.None => Akka.Event.LogLevel.ErrorLevel,
        _ => Akka.Event.LogLevel.WarningLevel
    };
}
