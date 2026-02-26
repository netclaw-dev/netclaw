using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Channels;
using Netclaw.Cli;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
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
        if (mode is "doctor")
            builder.Services.AddDoctorChecks();

        // Suppress framework console logging
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // TODO: init → Termina TUI wizard (Task 1.22)
        if (mode is "init")
        {
            Console.WriteLine("netclaw init: not yet implemented");
            return;
        }

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DoctorRunner>();
        var result = await runner.RunAsync();

        WriteDoctorResult(result);
        Environment.ExitCode = result.ExitCode;
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

    // ── Interactive / headless modes (daemon-backed via SignalR) ──
    var webBuilder = WebApplication.CreateBuilder(args);
    webBuilder.WebHost.UseUrls("http://127.0.0.1:0");

    var sharedPaths = ConfigureConfigServices(webBuilder.Services, webBuilder.Configuration);
    ConfigureCliChatServices(webBuilder.Services, webBuilder.Configuration);

    // Suppress framework console logging — console is reserved for the chat UI
    webBuilder.Logging.ClearProviders();
    webBuilder.Logging.SetMinimumLevel(LogLevel.Warning);

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

static void WriteDoctorResult(DoctorRunResult result)
{
    foreach (var check in result.Results)
    {
        var prefix = check.Severity switch
        {
            DoctorSeverity.Pass => "[PASS]",
            DoctorSeverity.Warning => "[WARN]",
            DoctorSeverity.Error => "[FAIL]",
            _ => "[INFO]"
        };

        Console.WriteLine($"{prefix} {check.Name}: {check.Message}");
        if (!string.IsNullOrWhiteSpace(check.Remediation))
            Console.WriteLine($"       fix: {check.Remediation}");
    }

    Console.WriteLine();
    Console.WriteLine($"doctor exit code: {result.ExitCode}");
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

    return paths;
}

// ═══════════════════════════════════════════════════════════════════════
// Daemon-backed CLI services (SignalR thin client)
// ═══════════════════════════════════════════════════════════════════════

static void ConfigureCliChatServices(IServiceCollection services, IConfigurationManager configuration)
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

    var daemonEndpoint =
        configuration["Daemon:Endpoint"]
        ?? Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
        ?? "http://127.0.0.1:5199";
    services.AddSingleton(new DaemonClient(daemonEndpoint));
}
