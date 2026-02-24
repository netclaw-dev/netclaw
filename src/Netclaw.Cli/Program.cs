using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.Channels;
using Netclaw.Cli;
using Netclaw.Cli.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Termina.Hosting;

try
{
    await RunAsync(args);
}
catch (Exception ex)
{
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

    // ── Lightweight modes (no Akka, no persistence) ──
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

    // ── Daemon management ──
    if (mode is "daemon")
    {
        var subcommand = args.Length > 1 ? args[1] : "help";
        var paths = new NetclawPaths();
        paths.EnsureDirectoriesExist();
        var manager = new DaemonManager(paths, TimeProvider.System);

        switch (subcommand)
        {
            case "start":
                var startResult = manager.Start();
                WriteDaemonResult(startResult);
                return;

            case "stop":
                var stopResult = await manager.StopAsync();
                WriteDaemonResult(stopResult);
                return;

            case "status":
                var status = manager.GetStatus();
                Console.WriteLine(status.Message);
                return;

            case "install":
                var installResult = await manager.InstallAsync();
                WriteDaemonResult(installResult);
                return;

            case "uninstall":
                var uninstallResult = await manager.UninstallAsync();
                WriteDaemonResult(uninstallResult);
                return;

            default:
                Console.WriteLine("Usage: netclaw daemon [start|stop|status|install|uninstall]");
                Environment.ExitCode = 1;
                return;
        }
    }

    // ── Config management stubs ──
    if (mode is "config")
    {
        Console.WriteLine("netclaw config: not yet implemented");
        return;
    }

    // ── Interactive / headless modes (transitional: full Akka stack in-process) ──
    // Task 1.28 refactors these to connect to daemon via SignalR instead.
    var webBuilder = WebApplication.CreateBuilder(args);

    // Transitional: use port 0 (random) to avoid conflict with daemon on 5199.
    // Removed in Task 1.28 when CLI switches to SignalR client.
    webBuilder.WebHost.UseUrls("http://127.0.0.1:0");

    var sharedPaths = ConfigureConfigServices(webBuilder.Services, webBuilder.Configuration);
    ConfigureDaemonServices(webBuilder.Services, webBuilder.Configuration, sharedPaths);

    // Suppress framework console logging — console is reserved for the chat UI
    webBuilder.Logging.ClearProviders();
    webBuilder.Logging.SetMinimumLevel(LogLevel.Warning);

    // SignalR (transitional — needed for in-process service stack)
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

        default:
            // Treat unknown commands as "chat" for backward compatibility
            webBuilder.Services.AddTermina("/chat", termina =>
            {
                termina.RegisterRoute<ChatPage, ChatViewModel>("/chat");
            });
            break;
    }

    var app = webBuilder.Build();

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
            Netclaw CLI crash at {DateTime.UtcNow:O}

            {ex}
            """);

        Console.Error.WriteLine($"Fatal error — crash log written to {crashPath}");
    }
    catch
    {
        Console.Error.WriteLine($"Fatal error (could not write crash log): {ex}");
    }
}

static void WriteDaemonResult(DaemonResult result)
{
    Console.WriteLine(result.Message);
    if (!result.Success)
        Environment.ExitCode = 1;
}

// ═══════════════════════════════════════════════════════════════════════
// Shared configuration services (all modes)
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

    // Providers and model resolution
    var providers = configuration.GetSection("Providers")
        .Get<Dictionary<string, ProviderEntry>>()
        ?? new() { ["local-ollama"] = new ProviderEntry() };
    var models = configuration.GetSection("Models")
        .Get<ModelSelection>() ?? new ModelSelection();

    var factory = new ChatClientFactory(providers);
    var clientProvider = new NetclawChatClientProvider(factory, models);
    services.AddSingleton<IChatClientProvider>(clientProvider);

    return paths;
}

// ═══════════════════════════════════════════════════════════════════════
// Transitional daemon services (removed in Task 1.28 when CLI uses SignalR)
// ═══════════════════════════════════════════════════════════════════════

static void ConfigureDaemonServices(IServiceCollection services, IConfigurationManager configuration, NetclawPaths paths)
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
}
