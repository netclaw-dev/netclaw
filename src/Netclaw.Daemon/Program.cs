using System.Buffers.Text;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Akka.Persistence.Sql.Hosting;
using Microsoft.AspNetCore.RateLimiting;
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
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Webhooks;
using Netclaw.Search;
using Netclaw.Security;
using static Microsoft.Extensions.Logging.LogLevel;

var bootstrapPaths = new NetclawPaths();
bootstrapPaths.EnsureDirectoriesExist();
using var crashMonitor = DaemonCrashMonitor.Register(bootstrapPaths);

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
    var daemonLogLevel = builder.ConfigureNetclawLogging();
    builder.AddNetclawTelemetry();
    ConfigureDaemonServices(builder.Services, builder.Configuration, paths, daemonLogLevel, daemonConfig);

    // Authentication — a PolicyScheme selector is the default scheme.
    // It routes to DeviceBearer when an Authorization: Bearer header is present,
    // otherwise to Loopback (local operator).  This ensures [Authorize] endpoints
    // are reachable by both loopback clients and paired remote devices.
    builder.Services.AddSingleton<DeviceRegistry>();
    builder.Services.AddSingleton<PairingCodeService>();
    builder.Services.AddSingleton<PairingExchangeGuard>();
    builder.Services.AddSingleton<IRemoteAuthSchemeRegistration, DevicePairingSchemeRegistration>();
    builder.Services.AddNetclawAuthSchemes();
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

    // Device pairing exchange — unauthenticated, rate-limited, with per-IP lockout guard.
    // Accepts a time-limited pairing code and a device name; returns a bearer token on success.
    app.MapPost("/api/pair/exchange", async (
        HttpContext httpContext,
        PairingCodeExchangeRequest request,
        PairingCodeService pairingCodeService,
        PairingExchangeGuard exchangeGuard,
        DeviceRegistry deviceRegistry,
        TimeProvider timeProvider,
        CancellationToken ct) =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress;

        // Layer 1: Per-IP failure lockout — blocked IPs get 429 before any processing.
        if (exchangeGuard.IsBlocked(remoteIp))
        {
            var retryAfter = exchangeGuard.GetRetryAfterSeconds(remoteIp);
            httpContext.Response.Headers.RetryAfter = retryAfter?.ToString() ?? "900";
            return Results.Json(
                new { error = "Too many failed attempts. Try again later." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Layer 2: No-code-pending gate — if no code exists, hide the endpoint entirely.
        if (pairingCodeService.GetPendingExpiry() is null)
            return Results.NotFound();

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName))
            return Results.BadRequest(new { error = "code and deviceName are required." });

        if (!pairingCodeService.TryConsume(request.Code))
        {
            exchangeGuard.RecordFailure(remoteIp);
            return Results.Json(
                new { error = "Invalid, expired, or already-used pairing code." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);

        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var tokenHash = DeviceRegistry.ComputeTokenHash(rawToken, saltHex);

        var now = timeProvider.GetUtcNow();
        var device = new PairedDevice
        {
            Name = request.DeviceName.Trim(),
            TokenHash = tokenHash,
            Salt = saltHex,
            CreatedAt = now,
            LastUsedAt = now,
        };

        try
        {
            await deviceRegistry.AddAsync(device, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }

        return Results.Ok(new { token = rawToken });
    }).RequireRateLimiting("pairing-exchange").AllowAnonymous();

    // Device registry management — authenticated (loopback or valid bearer token required).
    // Returns a sanitized view of paired devices (no TokenHash/Salt).
    app.MapGet("/api/pair/devices", async (DeviceRegistry deviceRegistry, CancellationToken ct) =>
    {
        var devices = await deviceRegistry.ListAsync(ct);
        var sanitized = devices.Select(d => new PairedDeviceInfoDto(d.Name, d.CreatedAt, d.LastUsedAt));
        return Results.Ok(sanitized);
    }).RequireAuthorization();

    app.MapDelete("/api/pair/devices/{name}", async (string name, DeviceRegistry deviceRegistry, CancellationToken ct) =>
    {
        var removed = await deviceRegistry.RemoveAsync(name, ct);
        return removed
            ? Results.NoContent()
            : Results.NotFound(new { error = $"Device '{name}' not found." });
    }).RequireAuthorization();

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
    }).RequireAuthorization();

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

            // Auto-reconnect the MCP server now that we have a valid token
            var serverName = oauthService.GetServerNameForState(state);
            if (serverName is not null)
            {
                var mcpManager = context.RequestServices.GetRequiredService<McpClientManager>();
                var reconnectLogger = context.RequestServices.GetRequiredService<ILogger<McpClientManager>>();
                _ = Task.Run(async () =>
                {
                    try { await mcpManager.TryReconnectAsync(serverName, CancellationToken.None); }
                    catch (Exception ex) { reconnectLogger.LogWarning(ex, "Post-OAuth reconnect failed for MCP server '{Name}'", serverName); }
                }, CancellationToken.None);
            }

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
    }).AllowAnonymous();

    app.MapGet("/api/mcp/statuses", (McpClientManager mcpManager) =>
    {
        var statuses = mcpManager.GetServerStatuses();
        var result = statuses.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                state = kvp.Value.State.ToString(),
                toolCount = kvp.Value.ToolCount,
                error = kvp.Value.ErrorMessage,
            });
        return Results.Ok(result);
    }).RequireAuthorization();

    app.MapGet("/api/mcp/tools/{name}", (string name, McpClientManager mcpManager) =>
    {
        var tools = mcpManager.GetToolNames(name);
        return Results.Ok(tools);
    }).RequireAuthorization();

    app.MapGet("/api/mcp/oauth/status/{name}", (string name, McpOAuthService oauthService) =>
    {
        var status = oauthService.GetFlowStatus(name);
        return Results.Ok(new { status = status.ToString() });
    }).RequireAuthorization();

    app.MapGet("/api/mcp/oauth/status-by-state/{state}", (string state, McpOAuthService oauthService) =>
    {
        var status = oauthService.GetFlowStatusByState(state);
        // Tokens are persisted daemon-side — never expose them over HTTP.
        return Results.Ok(new { status = status.ToString() });
    }).RequireAuthorization();

    app.MapProviderOAuthEndpoints();

    // Daemon lifecycle endpoint — CLI calls this before sending SIGTERM.
    // Config-triggered restart coordination happens inside DaemonRestartCoordinator.
    app.MapPost("/api/lifecycle/shutdown", (
        DaemonLifecycleNotifier notifier,
        HttpRequest request) =>
    {
        var reason = request.Query["reason"].ToString();
        if (string.IsNullOrEmpty(reason))
            return Results.BadRequest(new { error = "reason query parameter is required" });

        notifier.NotifyShutdown(reason);
        return Results.Ok(new { reason, pid = Environment.ProcessId });
    }).RequireAuthorization();

    // Register tools that need DI-resolved dependencies after the container is built.
    ChannelToolRegistration.RegisterChannelTools(app.Services);
    SkillToolRegistration.RegisterSkillTools(app.Services);

    // Reminder REST API
    MapReminderEndpoints(app);

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

    // Reminders
    var reminderConfig = configuration.GetSection("Reminders")
        .Get<ReminderConfig>() ?? new ReminderConfig();
    services.AddSingleton(reminderConfig);
    services.AddSingleton<ReminderDefinitionStore>();
    services.AddSingleton<ReminderHistoryStore>();

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

    // Search backend selection
    var searchConfig = configuration.GetSection("Search")
        .Get<SearchConfig>() ?? new SearchConfig();
    var searchBackend = CreateSearchBackend(searchConfig);

    var writeDenyList = new[]
    {
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

    var shellCommandPolicy = new ShellCommandPolicy(toolConfig.HardDenyPatterns);
    services.AddSingleton(shellCommandPolicy);

    var fileApprovalMatcher = new FilePathApprovalMatcher(paths.ConfigDirectory);
    var toolAccessPolicy = new ToolAccessPolicy(
        toolConfig,
        effectivePolicyDefaults,
        shellCommandPolicy,
        fileApprovalMatcher);
    services.AddSingleton(toolAccessPolicy);

    var toolApprovalStore = new ToolApprovalStore(paths.ToolApprovalsPath);
    services.AddSingleton(toolApprovalStore);
    services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();

    var toolRegistry = new ToolRegistry();
    toolRegistry.WithFirstPartyTools(toolConfig, searchBackend, toolPathPolicy, shellCommandPolicy, toolAccessPolicy, paths, webhookRouteStore);

    // Skills system: seed built-in skills to .system/, register sync service
    CopyBuiltInSkills(paths.SystemSkillsDirectory);
    var skillRegistry = new SkillRegistry();

    // External skill sources (Claude Code, Open Code, custom paths)
    var externalSkillsConfig = configuration.GetSection("ExternalSkills")
        .Get<ExternalSkillsConfig>() ?? new ExternalSkillsConfig();
    var resolvedExternalSources = externalSkillsConfig.ResolveEnabledSources();
    services.AddSingleton(externalSkillsConfig);
    services.AddSingleton(resolvedExternalSources);

    // Scan native skills first (highest precedence), then external sources
    var initialSkillScan = SkillScanner.ScanAndMerge(paths.SkillsDirectory, resolvedExternalSources);
    skillRegistry.ReplaceAll(initialSkillScan.AcceptedSkills, initialSkillScan.Issues);
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

    // Schema migration hosted service must start before any memory consumer so
    // both akka-persistence migrations and memory table creation run first.
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<SchemaMigrationHostedService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SchemaMigrationHostedService>());

    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MemoryCurationWorkerService>());

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
        .Get<Dictionary<string, McpServerEntry>>() ?? new();
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

    // Dynamic tool index context layer — NOT part of the persisted system prompt.
    // Backed by system-managed shadow files on disk so tool metadata remains
    // discoverable and inspectable across daemon restarts.
    services.AddSingleton<McpShadowCatalogWriter>();
    services.AddSingleton<IContextLayerProvider>(_ =>
        new FileContextLayerProvider(paths.ToolIndexShadowPath, ContextLayerTiming.OnceAtStart));

    // Skill index context layer — compressed format pointing at files on disk, rebuilt by sync service
    var skillIndexLayer = new SkillIndexContextLayer();
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

    // Skill directory watcher — auto-rescan when skill files change on disk.
    // Covers native skills directory and all external sources.
    // Registered after SystemSkillSyncService so initial sync completes first.
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
        sp.GetService<IToolApprovalService>()));

    services.AddSingleton(sp => new SessionMemoryServices(
        sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
        sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
        sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
        sp.GetService<SQLiteMemoryStore>()));

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
            if (config.BraveApiKey is null || string.IsNullOrWhiteSpace(config.BraveApiKey.Value))
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

// ═══════════════════════════════════════════════════════════════════════
// Reminder REST API
// ═══════════════════════════════════════════════════════════════════════

static void MapReminderEndpoints(WebApplication app)
{
    var reminders = app.MapGroup("/api/reminders")
        .RequireAuthorization();

    static ReminderAudienceAuthorizationContext? ResolveReminderAuthorizationContext(ClaimsPrincipalMapper mapper, HttpContext httpContext)
    {
        var identity = mapper.Map(httpContext.User);
        if (identity.Principal is not PrincipalClassification.Operator)
            return null;

        return new ReminderAudienceAuthorizationContext(
            TrustAudience.Personal,
            $"{identity.Principal}/{identity.Transport}");
    }

    reminders.MapGet("", async (
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var response = await manager.Ask<Netclaw.Actors.Reminders.ReminderListResponse>(
            new Netclaw.Actors.Reminders.ListRemindersCommand(IncludeDisabled: false), TimeSpan.FromSeconds(10), ct);
        var projected = response.Reminders.Select(r => new
        {
            id = r.Id.Value,
            title = r.Title,
            enabled = r.Enabled,
            schedule = Netclaw.Actors.Reminders.ListRemindersTool.DescribeSchedule(r.Schedule),
            nextFire = Netclaw.Actors.Reminders.SetReminderTool.FormatNextFire(r.NextFire),
            audience = r.Audience?.ToWireValue(),
        });
        return Results.Ok(projected);
    });

    reminders.MapPost("", async (
        CreateReminderRequest request,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        IServiceProvider serviceProvider,
        ClaimsPrincipalMapper mapper,
        HttpContext httpContext,
        TimeProvider timeProvider,
        ReminderConfig reminderConfig,
        CancellationToken ct) =>
    {
        var manager = await actor.GetAsync(ct);
        var authorization = ResolveReminderAuthorizationContext(mapper, httpContext);

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
        var toolContext = new Netclaw.Tools.ToolExecutionContext(sessionId: null, sessionDirectory: null);
        toolContext.Audience = authorization?.SourceAudience?.ToWireValue();
        toolContext.ChannelType = "manual";
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Id"] = effectiveId,
                ["Name"] = request.Name,
                ["Prompt"] = request.Prompt,
                ["ScheduleType"] = request.ScheduleType,
                ["Schedule"] = request.Schedule,
                ["ReportToChannel"] = reportToChannel,
                ["NotifyInstructions"] = notifyInstructions,
                ["NotifyPolicy"] = request.NotifyPolicy,
                ["Audience"] = request.Audience
            }, toolContext, ct);

        return result.StartsWith("Error", StringComparison.Ordinal)
            ? Results.BadRequest(new { error = result })
            : Results.Ok(new { message = result });
    });

    reminders.MapPost("/validate", (
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

    reminders.MapPost("/import", async (
        ImportReminderRequest request,
        Akka.Hosting.IRequiredActor<Netclaw.Actors.Hosting.ReminderManagerActorKey> actor,
        ClaimsPrincipalMapper mapper,
        HttpContext httpContext,
        CancellationToken ct) =>
    {
        if (request.Definition is null)
            return Results.BadRequest(new { error = "Reminder definition is required." });

        var authorization = ResolveReminderAuthorizationContext(mapper, httpContext);

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
            new SaveReminderCommand(request.Definition, mode.Value, authorization),
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

    reminders.MapDelete("/{id}", async (
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

    reminders.MapPost("/{id}/disable", async (
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

    reminders.MapPost("/{id}/enable", async (
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

    reminders.MapGet("/{id}", async (
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
            notifyPolicy = r.NotifyPolicy.ToString().ToLowerInvariant(),
            sessionId = r.SessionId,
            reportToChannel = r.ReportToChannel,
            audience = r.Audience?.ToWireValue(),
        });
    });

    reminders.MapGet("/{id}/history", async (
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
    public string? NotifyPolicy { get; init; }
    public string? Audience { get; init; }
}

sealed record ImportReminderRequest
{
    public required ReminderDefinition Definition { get; init; }
    public string? WriteMode { get; init; }
}

/// <summary>
/// Request body for <c>POST /api/pair/exchange</c>.
/// </summary>
sealed record PairingCodeExchangeRequest(string Code, string DeviceName);

public partial class Program;
