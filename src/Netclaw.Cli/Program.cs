using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Cli;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Reminder;
using Netclaw.Cli.Secrets;
using Netclaw.Cli.Model;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Update;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Configuration.Secrets;
using Termina.Diagnostics;
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
    var parseResult = CliArgsParser.Parse(args);
    string? headlessPrompt = null;
    string mode;

    switch (parseResult.Kind)
    {
        case CliParseKind.NoArgs:
            WriteGeneralHelp();
            Environment.ExitCode = 2;
            return;
        case CliParseKind.Help:
            WriteGeneralHelp();
            return;
        case CliParseKind.Version:
            Console.WriteLine($"netclaw {BuildInfo.Version} (commit {BuildInfo.CommitHash}, built {BuildInfo.BuildTimestamp})");
            return;
        case CliParseKind.MissingPromptArg:
            Console.Error.WriteLine("netclaw: -p/--prompt requires an argument.");
            Console.Error.WriteLine("Usage: netclaw -p \"your prompt here\"");
            Environment.ExitCode = 1;
            return;
        case CliParseKind.Unknown:
            Console.Error.WriteLine($"netclaw: '{parseResult.Mode}' is not a netclaw command. See 'netclaw --help'.");
            WriteGeneralHelp();
            Environment.ExitCode = 2;
            return;
        case CliParseKind.Headless:
            headlessPrompt = parseResult.HeadlessPrompt;
            mode = "headless";
            break;
        default: // CliParseKind.Known
            mode = parseResult.Mode!;
            break;
    }

    // Fire-and-forget update check for non-TUI modes only.
    // TUI modes (chat, sessions, headless, init) must not run background checks
    // that write to Console, as it corrupts the terminal UI.
    if (mode is not ("chat" or "sessions" or "headless" or "init"))
        _ = UpdateCommand.BackgroundUpdateCheckAsync();

    // ── Lightweight modes (no Akka, no persistence) ──
    if (mode is "init" or "doctor")
    {
        if (args.Length > 1 && IsHelpToken(args[1]))
        {
            WriteDoctorHelp();
            return;
        }

        DoctorCommandOptions? doctorOptions = null;
        if (mode is "doctor")
        {
            try
            {
                doctorOptions = DoctorCommandOptions.Parse(args);
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[FAIL] doctor options: {ex.Message}");
                WriteDoctorHelp();
                Environment.ExitCode = 1;
                return;
            }
        }

        var builder = Host.CreateApplicationBuilder(args);
        ConfigureConfigServices(builder.Services, builder.Configuration);
        if (mode is "doctor")
        {
            builder.Services.AddHttpClient<ISlackProbe, SlackProbe>();
            builder.Services.AddDoctorChecks();
        }

        // Suppress framework console logging
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        if (mode is "init")
        {
            // Enable Termina trace logging for debugging TUI input/rendering issues
            var traceFile = Path.Combine(Path.GetTempPath(), "netclaw-init-trace.log");
            builder.Services.AddTerminaFileTracing(traceFile, TerminaTraceCategory.All, TerminaTraceLevel.Trace);
            Console.Error.WriteLine($"Trace log: {traceFile}");

            // Provider descriptors (includes IProviderProbe via registry)
            builder.Services.AddProviderDescriptors();
            builder.Services.AddHttpClient("OAuthDeviceFlow");
            builder.Services.AddSingleton(sp =>
                new OAuthDeviceFlowService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                    sp.GetService<TimeProvider>()));
            builder.Services.AddSingleton(sp =>
                new OpenAiDeviceFlowService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                    sp.GetService<TimeProvider>()));
            builder.Services.AddSingleton<DeviceFlowServiceFactory>();
            builder.Services.AddHttpClient<ISlackProbe, SlackProbe>();

            // Init wizard + chat page dependencies (daemon lifecycle + SignalR)
            var initPaths = new NetclawPaths();
            builder.Services.AddSingleton(initPaths);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<DaemonManager>();
            builder.Services.AddSingleton<IBrowserAutomationBootstrapper, BrowserAutomationBootstrapper>();

            // Register DaemonClient, ChatNavigationState, and SessionConfig for ChatPage
            // (uses freshly-written config from the wizard's WriteConfig)
            builder.Services.AddSingleton(new ChatNavigationState());
            builder.Services.AddSingleton(sp => new DaemonClient(sp.GetRequiredService<DaemonApi>().Endpoint));
            builder.Services.AddSingleton(sp =>
            {
                // Read the just-written config files for session settings
                var configBuilder = new ConfigurationBuilder()
                    .AddJsonFile(initPaths.NetclawConfigPath, optional: true, reloadOnChange: false)
                    .AddJsonFile(initPaths.SecretsPath, optional: true, reloadOnChange: false)
                    .AddEnvironmentVariables("NETCLAW_");
                var initConfig = configBuilder.Build();

                return SessionConfig.BindFromConfiguration(initConfig.GetSection("Session"));
            });
            builder.Services.AddSingleton(sp =>
            {
                var configBuilder = new ConfigurationBuilder()
                    .AddJsonFile(initPaths.NetclawConfigPath, optional: true, reloadOnChange: false)
                    .AddJsonFile(initPaths.SecretsPath, optional: true, reloadOnChange: false)
                    .AddEnvironmentVariables("NETCLAW_");
                var initConfig = configBuilder.Build();

                var models = initConfig.GetSection("Models")
                    .Get<ModelSelection>() ?? new ModelSelection();

                return new ModelCapabilities
                {
                    ModelId = models.Main.ModelId,
                    ContextWindowTokens = models.Main.ContextWindow ?? 32_768,
                    CompactionModelId = models.Compaction?.ModelId,
                };
            });

            builder.Services.AddTermina("/init", termina =>
            {
                termina.RegisterRoute<InitWizardPage, InitWizardViewModel>("/init");
                termina.RegisterRoute<ChatPage, ChatViewModel>("/chat");
            });

            var initApp = builder.Build();
            await initApp.RunAsync();
            return;
        }

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<DoctorRunner>();
        var fixService = scope.ServiceProvider.GetRequiredService<DoctorFixService>();

        DoctorFixPlan? fixPlan = null;
        if (doctorOptions!.Fix)
        {
            fixPlan = await fixService.BuildPlanAsync();
            if (doctorOptions.Format is DoctorOutputFormat.Text)
                WriteDoctorFixPlan(fixPlan, doctorOptions.DryRun);

            if (fixPlan.HasChanges && !doctorOptions.DryRun)
            {
                var shouldApply = doctorOptions.Yes || PromptForDoctorFixApply();
                if (shouldApply)
                    await fixService.ApplyAsync(fixPlan);
            }
        }

        var result = await runner.RunAsync();

        if (doctorOptions.Format is DoctorOutputFormat.Json)
            WriteDoctorJsonResult(result, fixPlan, doctorOptions);
        else
            WriteDoctorResult(result);

        // Hint about --fix when there are issues and fix wasn't requested
        if (!doctorOptions.Fix
            && result.ExitCode != 0
            && doctorOptions.Format is DoctorOutputFormat.Text)
        {
            fixPlan ??= await fixService.BuildPlanAsync();
            if (fixPlan.HasChanges)
                Console.WriteLine("hint: Some issues may be auto-fixable. Run `netclaw doctor --fix --dry-run` to preview.");
        }

        Environment.ExitCode = result.ExitCode;
        return;
    }

    if (mode is "status")
    {
        if (args.Length > 1 && IsHelpToken(args[1]))
        {
            WriteStatusHelp();
            return;
        }

        var statusAsJson = false;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--json")
            {
                statusAsJson = true;
                continue;
            }

            if (arg is "--format")
            {
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("[FAIL] status options: Missing value after --format. Expected text or json.");
                    WriteStatusHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                i++;
                var format = args[i];
                statusAsJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
                if (!statusAsJson && !string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[FAIL] status options: Unsupported format '{format}'. Expected text or json.");
                    WriteStatusHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                continue;
            }

            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                var format = arg.Substring("--format=".Length);
                statusAsJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
                if (!statusAsJson && !string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[FAIL] status options: Unsupported format '{format}'. Expected text or json.");
                    WriteStatusHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                continue;
            }

            Console.WriteLine($"[FAIL] status options: Unknown option '{arg}'.");
            WriteStatusHelp();
            Environment.ExitCode = 1;
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);
        ConfigureConfigServices(builder.Services, builder.Configuration);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var exitCode = await RunStatusAsync(scope.ServiceProvider, statusAsJson);
        Environment.ExitCode = exitCode;
        return;
    }

    if (mode is "stats")
    {
        if (args.Length > 1 && IsHelpToken(args[1]))
        {
            WriteStatsHelp();
            return;
        }

        var statsAsJson = false;
        var statsTui = false;
        int? statsDays = null;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--json")
            {
                statsAsJson = true;
                continue;
            }

            if (arg is "--tui")
            {
                statsTui = true;
                continue;
            }

            if (arg is "--all")
            {
                statsDays = 0;
                continue;
            }

            if (arg is "--days")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var d) || d < 1)
                {
                    Console.WriteLine("[FAIL] stats options: --days requires a positive integer.");
                    WriteStatsHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                i++;
                statsDays = d;
                continue;
            }

            if (arg is "--format")
            {
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("[FAIL] stats options: Missing value after --format. Expected text or json.");
                    WriteStatsHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                i++;
                var format = args[i];
                statsAsJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
                if (!statsAsJson && !string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[FAIL] stats options: Unsupported format '{format}'. Expected text or json.");
                    WriteStatsHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                continue;
            }

            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                var format = arg.Substring("--format=".Length);
                statsAsJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
                if (!statsAsJson && !string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[FAIL] stats options: Unsupported format '{format}'. Expected text or json.");
                    WriteStatsHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                continue;
            }

            Console.WriteLine($"[FAIL] stats options: Unknown option '{arg}'.");
            WriteStatsHelp();
            Environment.ExitCode = 1;
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);
        ConfigureConfigServices(builder.Services, builder.Configuration);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        if (statsTui)
        {
            builder.Services.AddSingleton(new StatsNavigationState { Days = statsDays ?? 7 });
            builder.Services.AddTermina("/stats", termina =>
            {
                termina.RegisterRoute<StatsPage, StatsViewModel>("/stats");
            });

            var statsApp = builder.Build();
            await statsApp.RunAsync();
            return;
        }

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var exitCode = await RunStatsAsync(scope.ServiceProvider, statsAsJson, statsDays);
        Environment.ExitCode = exitCode;
        return;
    }

    // ── Daemon management ──
    if (mode is "daemon")
    {
        var subcommand = args.Length > 1 ? args[1] : "help";
        if (IsHelpToken(subcommand))
            subcommand = "help";

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
                var stopResult = await manager.StopAsync("cli-stop");
                WriteDaemonResult(stopResult);
                return;

            case "status":
                var status = manager.GetStatus();
                Console.WriteLine(status.Message);
                if (status.IsRunning)
                    Console.WriteLine("Tip: run `netclaw status` for detailed runtime connector and telemetry health.");
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
                WriteDaemonHelp();
                Environment.ExitCode = 1;
                return;
        }
    }

    // ── MCP server management (list/auth use daemon for live status/OAuth) ──
    if (mode is "mcp")
    {
        var mcpSubcommand = args.Length > 1 ? args[1] : "help";

        if (mcpSubcommand is "tools" && args.Length <= 2)
        {
            // Bare `netclaw mcp tools` → TUI mode
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var traceFile = Path.Combine(Path.GetTempPath(), "netclaw-mcp-tools-trace.log");
            builder.Services.AddTerminaFileTracing(traceFile, TerminaTraceCategory.All, TerminaTraceLevel.Trace);

            builder.Services.AddTermina("/mcp-tools", t =>
                t.RegisterRoute<McpToolPermissionsPage, McpToolPermissionsViewModel>("/mcp-tools"));

            await builder.Build().RunAsync();
            return;
        }

        if (mcpSubcommand is "auth" or "list" or "tools")
        {
            // auth/list/tools need the daemon — spin up DI to get DaemonApi
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            using var mcpHost = builder.Build();
            var mcpPaths = mcpHost.Services.GetRequiredService<NetclawPaths>();
            var mcpDaemonApi = mcpHost.Services.GetRequiredService<DaemonApi>();
            Environment.ExitCode = await McpCommand.RunAsync(args, mcpPaths, mcpDaemonApi);
        }
        else
        {
            // All other subcommands are offline config-file operations
            var paths = new NetclawPaths();
            paths.EnsureDirectoriesExist();
            Environment.ExitCode = await McpCommand.RunAsync(args, paths);
        }

        return;
    }

    // ── Provider management ──
    if (mode is "provider")
    {
        var paths = new NetclawPaths();
        paths.EnsureDirectoriesExist();

        // Bare invocation → TUI; subcommands → plain CLI
        if (args.Length == 1)
        {
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Services.AddProviderDescriptors();
            builder.Services.AddHttpClient("OAuthDeviceFlow");
            builder.Services.AddSingleton(sp =>
                new OAuthDeviceFlowService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                    sp.GetService<TimeProvider>()));
            builder.Services.AddSingleton(sp =>
                new OpenAiDeviceFlowService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuthDeviceFlow"),
                    sp.GetService<TimeProvider>()));
            builder.Services.AddSingleton<DeviceFlowServiceFactory>();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var traceFile = Path.Combine(Path.GetTempPath(), "netclaw-provider-trace.log");
            builder.Services.AddTerminaFileTracing(traceFile, TerminaTraceCategory.All, TerminaTraceLevel.Trace);

            builder.Services.AddTermina("/provider", t =>
                t.RegisterRoute<ProviderManagerPage, ProviderManagerViewModel>("/provider"));

            await builder.Build().RunAsync();
            return;
        }

        Environment.ExitCode = await ProviderCommand.RunAsync(args, paths);
        return;
    }

    // ── Model management ──
    if (mode is "model")
    {
        var paths = new NetclawPaths();
        paths.EnsureDirectoriesExist();

        // Bare invocation → TUI; subcommands → plain CLI
        if (args.Length == 1)
        {
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Services.AddProviderDescriptors();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var traceFile = Path.Combine(Path.GetTempPath(), "netclaw-model-trace.log");
            builder.Services.AddTerminaFileTracing(traceFile, TerminaTraceCategory.All, TerminaTraceLevel.Trace);

            builder.Services.AddTermina("/model", t =>
                t.RegisterRoute<ModelManagerPage, ModelManagerViewModel>("/model"));

            await builder.Build().RunAsync();
            return;
        }

        Environment.ExitCode = await ModelCommand.RunAsync(args, paths);
        return;
    }

    // ── Reminder management ──
    if (mode is "reminder")
    {
        if (args.Length > 1 && args[1] is "ui" or "tui")
        {
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var traceFile = Path.Combine(Path.GetTempPath(), "netclaw-reminder-trace.log");
            builder.Services.AddTerminaFileTracing(traceFile, TerminaTraceCategory.All, TerminaTraceLevel.Trace);

            builder.Services.AddTermina("/reminder", t =>
                t.RegisterRoute<ReminderCreatePage, ReminderCreateViewModel>("/reminder"));

            await builder.Build().RunAsync();
            return;
        }

        // help and validate are offline — skip DI
        var reminderSub = args.Length > 1 ? args[1] : "help";
        if (reminderSub is "help" or "-h" or "--help" or "validate" || args.Length == 1)
        {
            Environment.ExitCode = await ReminderCommand.RunAsync(args, daemonApi: null);
        }
        else
        {
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            using var host = builder.Build();
            Environment.ExitCode = await ReminderCommand.RunAsync(args, host.Services.GetRequiredService<DaemonApi>());
        }
        return;
    }

    // ── Secrets management ──
    if (mode is "secrets")
    {
        var secretsPaths = new NetclawPaths();
        secretsPaths.EnsureDirectoriesExist();
        Environment.ExitCode = SecretsCommand.Run(args, secretsPaths);
        return;
    }

    // ── Config management stubs ──
    if (mode is "config")
    {
        Console.WriteLine("netclaw config: not yet implemented");
        return;
    }

    // ── Self-update ──
    if (mode is "update")
    {
        var paths = new NetclawPaths();
        paths.EnsureDirectoriesExist();
        Environment.ExitCode = await UpdateCommand.RunAsync(args, paths);
        return;
    }

    // ── Sessions single-shot mode ──
    if (mode is "sessions")
    {
        var onceMode = false;
        var sessionsJsonOutput = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once":
                    onceMode = true;
                    break;
                case "--json":
                    sessionsJsonOutput = true;
                    onceMode = true;
                    break;
                case "--help" or "-h" or "help":
                    WriteSessionsHelp();
                    return;
            }
        }

        if (onceMode)
        {
            var builder = Host.CreateApplicationBuilder(args);
            ConfigureConfigServices(builder.Services, builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            using var host = builder.Build();
            using var scope = host.Services.CreateScope();
            Environment.ExitCode = await RunSessionsOnceAsync(scope.ServiceProvider, sessionsJsonOutput);
            return;
        }
    }

    // ── Parse --resume flag for chat mode ──
    string? resumeSessionId = null;
    if (mode is "chat")
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--resume" or "-r")
            {
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("[FAIL] chat options: Missing session ID after --resume.");
                    WriteChatHelp();
                    Environment.ExitCode = 1;
                    return;
                }

                resumeSessionId = args[++i];
                continue;
            }

            if (args[i].StartsWith("--resume=", StringComparison.Ordinal))
            {
                resumeSessionId = args[i]["--resume=".Length..];
                continue;
            }

            if (IsHelpToken(args[i]))
            {
                WriteChatHelp();
                return;
            }
        }
    }

    // ── Interactive / headless modes (daemon-backed via SignalR) ──
    var webBuilder = WebApplication.CreateBuilder(args);
    webBuilder.WebHost.UseUrls("http://127.0.0.1:0");

    var sharedPaths = ConfigureConfigServices(webBuilder.Services, webBuilder.Configuration);
    ConfigureCliChatServices(webBuilder.Services, webBuilder.Configuration);

    // Shared navigation state for passing resume session ID to ChatViewModel
    var navState = new ChatNavigationState { ResumeSessionId = resumeSessionId };
    webBuilder.Services.AddSingleton(navState);

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

        case "sessions":
            webBuilder.Services.AddTermina("/sessions", termina =>
            {
                termina.RegisterRoute<SessionsPage, SessionsViewModel>("/sessions");
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
            Console.Error.WriteLine($"netclaw: internal error: unhandled mode '{mode}'");
            Environment.ExitCode = 2;
            return;
    }

    var app = webBuilder.Build();
    await app.RunAsync();
}

static void WriteCrashLog(Exception ex)
{
    CrashLogWriter.Write(ex, "CLI");
}

static void WriteDaemonResult(DaemonResult result)
{
    Console.WriteLine(result.Message);
    if (!result.Success)
        Environment.ExitCode = 1;
}

static bool IsHelpToken(string token)
{
    return token is "help" or "-h" or "--help";
}

static void WriteGeneralHelp()
{
    Console.WriteLine("Usage: netclaw <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  chat                     Interactive TUI chat");
    Console.WriteLine("  chat --resume <id>       Resume an existing session by ID");
    Console.WriteLine("  sessions                 Browse and resume recent sessions (TUI)");
    Console.WriteLine("  sessions --once          List sessions and exit (no TUI, plain text or JSON)");
    Console.WriteLine("  -p, --prompt <text>      Headless single-prompt mode");
    Console.WriteLine("  doctor                   Configuration diagnostics (offline)");
    Console.WriteLine("  status                   Runtime status from daemon health JSON endpoint");
    Console.WriteLine("  stats                    Usage activity statistics from daemon");
    Console.WriteLine("  daemon <subcommand>      Manage daemon lifecycle");
    Console.WriteLine("  mcp                      Manage MCP server profiles");
    Console.WriteLine("  provider                 Manage LLM providers (TUI) or use subcommands");
    Console.WriteLine("  model                    Manage model assignments (TUI) or use subcommands");
    Console.WriteLine("  reminder                 Manage scheduled reminders (daemon-required)");
    Console.WriteLine("  secrets                  Manage encrypted secrets (set key/value pairs)");
    Console.WriteLine("  init                     First-run setup wizard");
    Console.WriteLine("  update                   Check for and install updates");
    Console.WriteLine("  version, --version       Show CLI version");
    Console.WriteLine("  config                   Configuration management (planned)");
    Console.WriteLine();
    Console.WriteLine("Run `netclaw <command> --help` for details on any command.");
}

static void WriteDaemonHelp()
{
    Console.WriteLine("Usage: netclaw daemon <subcommand>");
    Console.WriteLine();
    Console.WriteLine("Subcommands:");
    Console.WriteLine("  start       Start daemon as a background process");
    Console.WriteLine("  stop        Stop daemon gracefully");
    Console.WriteLine("  status      Show daemon process status");
    Console.WriteLine("  install     Install systemd user service (Linux)");
    Console.WriteLine("  uninstall   Remove systemd user service (Linux)");
}

static void WriteDoctorHelp()
{
    Console.WriteLine("Usage: netclaw doctor");
    Console.WriteLine();
    Console.WriteLine("Runs configuration diagnostics and daemon-backed MCP verification when available:");
    Console.WriteLine("  - netclaw.json schema validation (versioned by configVersion)");
    Console.WriteLine("  - secrets.json syntax validation");
    Console.WriteLine("  - MCP runtime auth/connectivity status from the daemon");
    Console.WriteLine("  - explicit offline MCP connectivity checks when daemon status is unavailable");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --format <text|json>   Output format (default: text)");
    Console.WriteLine("  --fix                  Apply safe automatic fixes");
    Console.WriteLine("  --dry-run              Show fixes without writing files (implies --fix)");
    Console.WriteLine("  --yes, -y              Apply fixes without confirmation prompt");
    Console.WriteLine();
    Console.WriteLine("Exit codes:");
    Console.WriteLine("  0  all checks passed");
    Console.WriteLine("  1  one or more checks failed");
    Console.WriteLine("  2  warnings only");
}

static void WriteChatHelp()
{
    Console.WriteLine("Usage: netclaw chat [options]");
    Console.WriteLine();
    Console.WriteLine("Start an interactive TUI chat session with the daemon.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --resume, -r <id>   Resume an existing session by its catalog ID");
    Console.WriteLine("                      Use `netclaw sessions` to browse available sessions");
}

static void WriteStatusHelp()
{
    Console.WriteLine("Usage: netclaw status");
    Console.WriteLine();
    Console.WriteLine("Queries daemon runtime status from /api/health/status and prints:");
    Console.WriteLine("  - overall health");
    Console.WriteLine("  - daemon process uptime");
    Console.WriteLine("  - connector health (including disabled connectors)");
    Console.WriteLine("  - persistence and telemetry summary");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --json                 Alias for --format json");
    Console.WriteLine("  --format <text|json>   Output format (default: text)");
}

static void WriteStatsHelp()
{
    Console.WriteLine("Usage: netclaw stats [options]");
    Console.WriteLine();
    Console.WriteLine("Shows usage activity statistics from the running daemon:");
    Console.WriteLine("  - token consumption (input, output)");
    Console.WriteLine("  - session and turn counts");
    Console.WriteLine("  - memory formation and recall counts");
    Console.WriteLine("  - skill auto-load counts");
    Console.WriteLine("  - memory store statistics");
    Console.WriteLine("  - Slack activity counters");
    Console.WriteLine("  - reminder statistics");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --days <N>             Show trailing N-day daily breakdown");
    Console.WriteLine("  --all                  Show all-time daily breakdown");
    Console.WriteLine("  --tui                  Visual dashboard (default: last 7 days)");
    Console.WriteLine("  --json                 Alias for --format json");
    Console.WriteLine("  --format <text|json>   Output format (default: text)");
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

static void WriteDoctorJsonResult(DoctorRunResult result, DoctorFixPlan? fixPlan, DoctorCommandOptions options)
{
    var payload = new
    {
        exitCode = result.ExitCode,
        checks = result.Results.Select(r => new
        {
            name = r.Name,
            severity = r.Severity.ToString().ToLowerInvariant(),
            message = r.Message,
            remediation = r.Remediation
        }),
        fix = new
        {
            requested = options.Fix,
            dryRun = options.DryRun,
            changedFiles = fixPlan?.Fixes.Count ?? 0,
            files = (fixPlan?.Fixes ?? []).Select(f => new
            {
                path = f.FilePath,
                description = f.Description
            })
        }
    };

    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true
    }));
}

static void WriteDoctorFixPlan(DoctorFixPlan plan, bool dryRun)
{
    if (!plan.HasChanges)
    {
        Console.WriteLine("No safe autofixes available.");
        return;
    }

    Console.WriteLine(dryRun
        ? "Planned fixes (dry-run):"
        : "Planned fixes:");

    foreach (var fix in plan.Fixes)
    {
        Console.WriteLine($"- {fix.FilePath}");
        Console.WriteLine($"  {fix.Description}");
        WriteSimpleDiff(fix.OriginalText, fix.UpdatedText);
    }
}

static bool PromptForDoctorFixApply()
{
    Console.Write("Apply these fixes? [y/N]: ");
    var response = Console.ReadLine();
    return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
           || string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
}

static void WriteSimpleDiff(string original, string updated)
{
    var oldLines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var newLines = updated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    Console.WriteLine("  --- before");
    Console.WriteLine("  +++ after");

    var max = Math.Max(oldLines.Length, newLines.Length);
    for (var i = 0; i < max; i++)
    {
        var oldLine = i < oldLines.Length ? oldLines[i] : null;
        var newLine = i < newLines.Length ? newLines[i] : null;

        if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
            continue;

        if (oldLine is not null)
            Console.WriteLine($"  - {oldLine}");
        if (newLine is not null)
            Console.WriteLine($"  + {newLine}");
    }
}

static async Task<int> RunStatusAsync(IServiceProvider services, bool jsonOutput)
{
    var api = services.GetRequiredService<DaemonApi>();
    var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

    // Start CLI update check concurrently with daemon status fetch (3s timeout, non-blocking).
    using var updateCts = new CancellationTokenSource();
    var updateClient = httpClientFactory.CreateClient();
    var updateTask = StatusUpdateChecker.CheckAsync(updateClient, BuildInfo.Version, updateCts.Token);

    try
    {
        var status = await api.GetStatusAsync();

        if (status is null)
        {
            await updateCts.CancelAsync();
            Console.WriteLine("[FAIL] status: daemon returned an empty status payload.");
            return 1;
        }

        var cliUpdate = await updateTask;

        if (jsonOutput)
        {
            var node = JsonSerializer.SerializeToNode(
                status, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var updateNode = (node["update"] as JsonObject) ?? new JsonObject();
            var updateAvailable = string.Equals(cliUpdate.State, "update-available", StringComparison.Ordinal);
            updateNode["available"] = updateAvailable;
            updateNode["state"] = cliUpdate.State;
            updateNode["currentVersion"] = cliUpdate.CurrentVersion;
            updateNode["latestVersion"] = updateAvailable ? cliUpdate.LatestVersion : null;
            updateNode["releaseNotesUrl"] = updateAvailable ? cliUpdate.ReleaseNotesUrl : null;
            node["update"] = updateNode;
            Console.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            WriteStatusResult(status, api.Endpoint, cliUpdate);
        }

        return status.Overall.ToLowerInvariant() switch
        {
            "healthy" => 0,
            "degraded" => 2,
            _ => 1
        };
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        await updateCts.CancelAsync();
        Console.WriteLine($"[FAIL] status: daemon returned {(int)ex.StatusCode} from {api.Endpoint}");
        Console.WriteLine("       fix: run `netclaw daemon status` and `netclaw daemon start`.");
        return 1;
    }
    catch (Exception ex)
    {
        await updateCts.CancelAsync();
        Console.WriteLine($"[FAIL] status: unable to reach daemon at {api.Endpoint}: {ex.Message}");
        Console.WriteLine("       fix: run `netclaw daemon start` and retry.");
        return 1;
    }
}

static async Task<int> RunSessionsOnceAsync(IServiceProvider services, bool jsonOutput)
{
    var api = services.GetRequiredService<DaemonApi>();

    try
    {
        var sessions = await api.ListSessionsAsync();

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(sessions, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        else
        {
            if (sessions.Count == 0)
            {
                Console.WriteLine("No sessions found.");
            }
            else
            {
                foreach (var session in sessions)
                {
                    var title = string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title;
                    var lastActivity = DateTimeOffset.FromUnixTimeMilliseconds(session.LastActivity)
                        .ToString("yyyy-MM-dd HH:mm");
                    Console.WriteLine(
                        $"{session.SessionId}  {title}  [{session.Status}]  turns={session.TurnCount}  last={lastActivity}");
                }
            }
        }

        return 0;
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        Console.WriteLine($"[FAIL] sessions: daemon returned {(int)ex.StatusCode} from {api.Endpoint}");
        Console.WriteLine("       fix: run `netclaw daemon status` and `netclaw daemon start`.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] sessions: unable to reach daemon at {api.Endpoint}: {ex.Message}");
        Console.WriteLine("       fix: run `netclaw daemon start` and retry.");
        return 1;
    }
}

static void WriteSessionsHelp()
{
    Console.WriteLine("Usage: netclaw sessions [options]");
    Console.WriteLine();
    Console.WriteLine("Browse and resume recent sessions.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --once          List sessions and exit (no TUI). Requires daemon.");
    Console.WriteLine("  --json          Output as JSON (implies --once).");
    Console.WriteLine();
    Console.WriteLine("Exit codes (--once):");
    Console.WriteLine("  0  sessions listed successfully");
    Console.WriteLine("  1  daemon unavailable or request failed");
}

static void WriteStatusResult(DaemonRuntimeStatus.Response status, string endpoint, StatusUpdateResult? cliUpdate = null)
{
    Console.WriteLine($"overall: {status.Overall}");
    Console.WriteLine($"version: {status.Build.Version} (commit {status.Build.CommitHash}, built {status.Build.BuildTimestamp})");
    Console.WriteLine($"daemon: PID {status.Process.Pid}, uptime {FormatUptime(status.Process.UptimeSeconds)}, endpoint {endpoint}");
    Console.WriteLine($"persistence: {status.Persistence.Provider}");

    if (status.Memory is { } memory)
    {
        var memoryDetail = $"{memory.Provider} ({memory.Status})";
        Console.WriteLine($"memory: {memoryDetail}");
    }

    Console.WriteLine($"telemetry: {(status.Telemetry.Enabled ? "enabled" : "disabled")}" +
                      (status.Telemetry.Enabled && !string.IsNullOrWhiteSpace(status.Telemetry.OtlpEndpoint)
                          ? $" ({status.Telemetry.OtlpEndpoint})"
                          : string.Empty));

    if (status.Telemetry.SlackCounters is { } counters)
    {
        Console.WriteLine(
            $"slack counters: recv={counters.EventsReceived} routed={counters.EventsRouted} dropped={counters.EventsDropped} enqueued={counters.MessagesEnqueued} replied={counters.RepliesPosted} rejected={counters.RepliesRejected} reply_failed={counters.RepliesFailed}");
    }

    if (status.Model is { } model)
    {
        Console.WriteLine($"model: {model.DisplayName ?? model.ModelId} (provider: {model.Provider}, context: {model.ContextWindow:N0} tokens)");
        Console.WriteLine($"  input: {model.InputModalities}");
        Console.WriteLine($"  output: {model.OutputModalities}");
    }

    Console.WriteLine("connectors:");
    foreach (var connector in status.Connectors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
    {
        var enabled = connector.Enabled ? "enabled" : "disabled";
        Console.WriteLine($"- {connector.Key}: {connector.Status} ({enabled})");
        if (!string.IsNullOrWhiteSpace(connector.Message))
            Console.WriteLine($"    {connector.Message}");
    }

    // Resolve update state: prefer CLI check (freshest), fall back to daemon's cached result
    var updateState = cliUpdate?.State ?? status.Update?.State ?? "unknown";
    var updateCurrentVersion = cliUpdate?.CurrentVersion ?? status.Update?.CurrentVersion ?? status.Build.Version;
    var updateLatestVersion = cliUpdate?.LatestVersion ?? status.Update?.LatestVersion;
    var updateReleaseNotesUrl = cliUpdate?.ReleaseNotesUrl ?? status.Update?.ReleaseNotesUrl;

    Console.WriteLine();
    switch (updateState)
    {
        case "update-available":
            Console.WriteLine($"update: UPDATE AVAILABLE — v{updateCurrentVersion} → v{updateLatestVersion}");
            Console.WriteLine("  Run: netclaw update");
            if (updateReleaseNotesUrl is not null)
                Console.WriteLine($"  Release notes: {updateReleaseNotesUrl}");
            break;
        case "up-to-date":
            Console.WriteLine($"update: up-to-date (v{updateCurrentVersion})");
            break;
        default:
            var errorHint = cliUpdate?.ErrorDetail ?? status.Update?.ErrorDetail;
            var detail = errorHint is not null ? $" [{errorHint}]" : "";
            Console.WriteLine($"update: unknown (check failed{detail} — run 'netclaw update --check' to retry)");
            break;
    }
}

static string FormatUptime(long uptimeSeconds)
{
    var uptime = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));

    if (uptime.TotalDays >= 1)
        return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
    if (uptime.TotalHours >= 1)
        return $"{uptime.Hours}h {uptime.Minutes}m";

    return $"{uptime.Minutes}m {uptime.Seconds}s";
}

// ═══════════════════════════════════════════════════════════════════════
// Stats command
// ═══════════════════════════════════════════════════════════════════════

static async Task<int> RunStatsAsync(IServiceProvider services, bool jsonOutput, int? days = null)
{
    var api = services.GetRequiredService<DaemonApi>();

    try
    {
        var stats = await api.GetStatsAsync(days);

        if (stats is null)
        {
            Console.WriteLine("[FAIL] stats: daemon returned an empty stats payload.");
            return 1;
        }

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(stats, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }
        else
        {
            WriteStatsResult(stats, days);
        }

        return 0;
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        Console.WriteLine($"[FAIL] stats: daemon returned {(int)ex.StatusCode} from {api.Endpoint}");
        Console.WriteLine("       fix: run `netclaw daemon start` and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] stats: unable to reach daemon at {api.Endpoint}: {ex.Message}");
        Console.WriteLine("       fix: run `netclaw daemon start` and retry.");
        return 1;
    }
}

static void WriteStatsResult(DaemonStats.Response stats, int? days)
{
    // Header
    if (days is > 0)
        Console.WriteLine($"netclaw stats — last {days} days");
    else if (days == 0)
        Console.WriteLine("netclaw stats — all time");
    else
        Console.WriteLine($"netclaw stats — usage since daemon started ({FormatUptime(stats.Process.UptimeSeconds)} ago)");
    Console.WriteLine();

    // Process-lifetime counters
    Console.WriteLine("this process:");
    Console.WriteLine($"  tokens: in={stats.Tokens.InputTokensTotal:N0} out={stats.Tokens.OutputTokensTotal:N0}");
    Console.WriteLine($"  turns: {stats.Tokens.TurnsCompletedTotal:N0}    memories: formed={stats.Tokens.MemoriesFormedTotal:N0} recalled={stats.Tokens.MemoriesRecalledTotal:N0}    skills loaded: {stats.Tokens.SkillsLoadedTotal:N0}");
    Console.WriteLine();

    // Daily breakdown table (when requested)
    if (stats.DailyBreakdown is { Count: > 0 })
    {
        WriteDailyTable(stats.DailyBreakdown);
        Console.WriteLine();
    }

    // Snapshot data
    Console.WriteLine("sessions:");
    Console.WriteLine($"  total: {stats.Sessions.TotalSessions}    active: {stats.Sessions.ActiveSessions}    turns (all-time): {stats.Sessions.TotalTurns:N0}");
    Console.WriteLine();

    Console.WriteLine($"memory ({stats.Memory.Status}):");
    if (stats.Memory.Status is not "unavailable")
    {
        Console.WriteLine($"  anchors: {stats.Memory.AnchorCount}    documents: {stats.Memory.DocumentCount}    records: {stats.Memory.RecordCount}    edges: {stats.Memory.EdgeCount}");
        Console.WriteLine($"  pending checkpoints: {stats.Memory.PendingCheckpoints}");
    }
    Console.WriteLine();

    Console.WriteLine("skills:");
    Console.WriteLine($"  available: {stats.Skills.TotalAvailable}");
    Console.WriteLine();

    Console.WriteLine("slack:");
    Console.WriteLine($"  events: recv={stats.SlackActivity.EventsReceived} routed={stats.SlackActivity.EventsRouted} dropped={stats.SlackActivity.EventsDropped}");
    Console.WriteLine($"  replies: posted={stats.SlackActivity.RepliesPosted} rejected={stats.SlackActivity.RepliesRejected} failed={stats.SlackActivity.RepliesFailed}");

    if (stats.Reminders is { } reminders)
    {
        Console.WriteLine();
        Console.WriteLine("reminders:");
        Console.WriteLine($"  scheduled: {reminders.ScheduledCount}    active: {reminders.ActiveExecutions}    failed: {reminders.FailedCount}");
    }
}

static void WriteDailyTable(List<DaemonStats.DailyRow> rows)
{
    // Header
    Console.WriteLine("date          in tokens    out tokens   turns   sessions   mem formed   mem recalled   skills");
    Console.WriteLine("----------   ----------   ----------   -----   --------   ---------   ------------   ------");

    long totalIn = 0, totalOut = 0, totalTurns = 0, totalSessions = 0;
    long totalFormed = 0, totalRecalled = 0, totalSkills = 0;

    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{row.Date}   {row.InputTokens,10:N0}   {row.OutputTokens,10:N0}   {row.Turns,5:N0}   {row.Sessions,8:N0}   {row.MemoriesFormed,9:N0}   {row.MemoriesRecalled,12:N0}   {row.SkillsLoaded,6:N0}");

        totalIn += row.InputTokens;
        totalOut += row.OutputTokens;
        totalTurns += row.Turns;
        totalSessions += row.Sessions;
        totalFormed += row.MemoriesFormed;
        totalRecalled += row.MemoriesRecalled;
        totalSkills += row.SkillsLoaded;
    }

    if (rows.Count > 1)
    {
        Console.WriteLine("----------   ----------   ----------   -----   --------   ---------   ------------   ------");
        Console.WriteLine(
            $"{"totals",-10}   {totalIn,10:N0}   {totalOut,10:N0}   {totalTurns,5:N0}   {totalSessions,8:N0}   {totalFormed,9:N0}   {totalRecalled,12:N0}   {totalSkills,6:N0}");
    }
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

    // Shared daemon HTTP API client — single endpoint resolution for all commands
    services.AddHttpClient();
    services.AddSingleton<DaemonApi>();

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

    // Session config: bind operator-facing settings
    var sessionConfig = SessionConfig.BindFromConfiguration(configuration.GetSection("Session"));
    services.AddSingleton(sessionConfig);
    services.AddSingleton(new ModelCapabilities
    {
        ModelId = models.Main.ModelId,
        ContextWindowTokens = models.Main.ContextWindow ?? 32_768,
        CompactionModelId = models.Compaction?.ModelId,
    });

    // DaemonClient uses the endpoint from DaemonApi (already registered in ConfigureConfigServices)
    services.AddSingleton(sp => new DaemonClient(sp.GetRequiredService<DaemonApi>().Endpoint));
}
