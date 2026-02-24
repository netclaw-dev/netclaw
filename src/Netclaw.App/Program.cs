using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.App;
using Netclaw.App.Configuration;
using Netclaw.App.Gateway;
using Netclaw.App.Services;
using Netclaw.App.Tui;
using Netclaw.Channels;
using Netclaw.Configuration;
using Termina.Hosting;

try
{
    await RunAsync(args);
}
catch (Exception ex)
{
    // Write crash log to ~/.netclaw/logs/ so fatal errors are always diagnosable
    WriteCrashLog(ex);
    throw;
}

static async Task RunAsync(string[] args)
{
    // ── Mode selection from CLI args ──
    var mode = args.Length > 0 ? args[0] : "chat";
    string? headlessPrompt = null;

    if (mode is "-p" or "--prompt")
    {
        headlessPrompt = args.Length > 1
            ? args[1]
            : throw new InvalidOperationException("Missing prompt argument after -p/--prompt");
        mode = "headless";
    }

    // ── Lightweight modes (no Akka, no persistence, no SignalR) ──
    if (mode is "init" or "doctor")
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureConfigServices(builder.Services, builder.Configuration);

        // Suppress framework console logging
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // TODO: init → Termina TUI wizard (Task 1.22)
        // TODO: doctor → health checks (Task 1.21)
        Console.WriteLine($"netclaw {mode}: not yet implemented");

        await builder.Build().RunAsync();
        return;
    }

    // ── Daemon modes (Akka, persistence, SignalR, tools) ──
    var webBuilder = WebApplication.CreateBuilder(args);

    // Use port 5199 to avoid conflicts with Aspire (5000) and other defaults
    webBuilder.WebHost.UseUrls("http://127.0.0.1:5199");

    ConfigureConfigServices(webBuilder.Services, webBuilder.Configuration);
    ConfigureDaemonServices(webBuilder.Services, webBuilder.Configuration);

    // Suppress framework console logging — session logs go to disk,
    // console is reserved for the chat UI
    webBuilder.Logging.ClearProviders();
    webBuilder.Logging.SetMinimumLevel(LogLevel.Warning);

    // SignalR for future remote clients (Blazor ops console)
    webBuilder.Services.AddSignalR();

    // Channel selection based on mode
    switch (mode)
    {
        case "chat":
            webBuilder.Services.AddTermina("/chat", termina =>
            {
                termina.RegisterRoute<ChatPage, ChatViewModel>("/chat");
            });
            break;

        case "headless":
            webBuilder.Services.AddSingleton<HeadlessChannel>(sp =>
                ActivatorUtilities.CreateInstance<HeadlessChannel>(sp, headlessPrompt!));
            webBuilder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<HeadlessChannel>());
            webBuilder.Services.AddSingleton<IChannel>(sp => sp.GetRequiredService<HeadlessChannel>());
            break;

        case "run":
            // Daemon only — no interactive channel. Slack adapter, scheduled tasks, etc.
            // TODO: Slack adapter (Task 1.23)
            break;

        default:
            // Treat unknown commands as "chat" for backward compatibility
            webBuilder.Services.AddTermina("/chat", termina =>
            {
                termina.RegisterRoute<ChatPage, ChatViewModel>("/chat");
            });
            break;
    }

    var app = webBuilder.Build();

    // Gateway surface (Phase 1 — minimal)
    app.MapHub<SessionHub>("/hub/session");
    app.MapGet("/api/health/ready", () => Results.Ok("healthy"));

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
            Netclaw crash at {DateTime.UtcNow:O}

            {ex}
            """);

        Console.Error.WriteLine($"Fatal error — crash log written to {crashPath}");
    }
    catch
    {
        // Last resort: write to stderr if we can't write the log file
        Console.Error.WriteLine($"Fatal error (could not write crash log): {ex}");
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Shared configuration services (all modes)
// ═══════════════════════════════════════════════════════════════════════

static void ConfigureConfigServices(IServiceCollection services, IConfigurationManager configuration)
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

    // Providers and model resolution
    var providers = configuration.GetSection("Providers")
        .Get<Dictionary<string, ProviderEntry>>()
        ?? new() { ["local-ollama"] = new ProviderEntry() };
    var models = configuration.GetSection("Models")
        .Get<ModelSelection>() ?? new ModelSelection();

    var factory = new ChatClientFactory(providers);
    var clientProvider = new NetclawChatClientProvider(factory, models);
    services.AddSingleton<IChatClientProvider>(clientProvider);
}

// ═══════════════════════════════════════════════════════════════════════
// Daemon-only services (run, chat, headless modes)
// ═══════════════════════════════════════════════════════════════════════

static void ConfigureDaemonServices(IServiceCollection services, IConfigurationManager configuration)
{
    // Resolve models for session config
    var models = configuration.GetSection("Models")
        .Get<ModelSelection>() ?? new ModelSelection();

    // Session config from resolved models
    var sessionSection = configuration.GetSection("Session");
    services.AddSingleton(new SessionConfig
    {
        ModelId = models.Main.ModelId,
        ContextWindowTokens = models.Main.ContextWindow ?? 32_768,
        CompactionModelId = models.Compaction?.ModelId,
        CompactionThreshold = sessionSection.GetValue("CompactionThreshold", 0.75),
        SnapshotInterval = sessionSection.GetValue("SnapshotInterval", 20),
        KeepRecentToolResults = sessionSection.GetValue("KeepRecentToolResults", 3),
        MaxToolIterationsPerTurn = sessionSection.GetValue("MaxToolIterationsPerTurn", 10),
    });

    // Tools (auto-bound, no required properties)
    var toolConfig = configuration.GetSection("Tools")
        .Get<ToolConfig>() ?? new ToolConfig();
    services.AddSingleton(toolConfig);

    var toolRegistry = new ToolRegistry();
    toolRegistry.WithFirstPartyTools(toolConfig);
    services.AddSingleton(toolRegistry);
    services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(toolRegistry));

    // System prompt (file-based, with first-run seed)
    var paths = new NetclawPaths();
    if (!File.Exists(paths.PersonalityPath))
        File.WriteAllText(paths.PersonalityPath,
            "You are Netclaw, a helpful homelab operations assistant. "
            + "Be concise and direct.");
    services.AddSingleton<ISystemPromptProvider>(
        new FileSystemPromptProvider(paths));

    // Akka.NET actor system
    services.AddAkka("netclaw", (akkaBuilder, sp) =>
    {
        akkaBuilder
            .ConfigureLoggers(setup =>
            {
                setup.ClearLoggers();
                setup.AddLoggerFactory();
                setup.LogLevel = Akka.Event.LogLevel.WarningLevel;
            })
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    });

    // Session pipeline (stream API for channels)
    services.AddSingleton<SessionPipeline>();

    // Config hot-reload watcher
    services.AddSingleton<ConfigWatcherService>();
    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConfigWatcherService>());
}
