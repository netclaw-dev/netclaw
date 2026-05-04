// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
using Netclaw.Cli.Discord;
using Netclaw.Cli.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Reminder;
using Netclaw.Cli.Secrets;
using Netclaw.Cli.Model;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Skills;
using Netclaw.Cli.Update;
using Netclaw.Cli.Webhooks;
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
        case CliParseKind.Unknown:
            Console.Error.WriteLine($"netclaw: '{parseResult.Mode}' is not a netclaw command. See 'netclaw --help'.");
            WriteGeneralHelp();
            Environment.ExitCode = 2;
            return;
        default: // CliParseKind.Known
            mode = parseResult.Mode!;
            break;
    }

    // Fire-and-forget update check for non-TUI modes only.
    // TUI modes (chat, sessions, headless, init) must not run background checks
    // that write to Console, as it corrupts the terminal UI.
    if (mode is not ("chat" or "sessions" or "headless" or "init"))
    {
        var backgroundUpdateConfig = BuildCliConfig();
        var backgroundDaemonConfig = DaemonConfig.BindFromConfiguration(backgroundUpdateConfig.GetSection("Daemon"));
        _ = UpdateCommand.BackgroundUpdateCheckAsync(backgroundDaemonConfig.DisableSelfUpdate);
    }

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
            builder.Services.AddHttpClient<IDiscordProbe, DiscordProbe>();
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
            builder.Services.AddHttpClient<IDiscordProbe, DiscordProbe>();

            // Init wizard + chat page dependencies (daemon lifecycle + SignalR)
            var initPaths = new NetclawPaths();
            builder.Services.AddSingleton(initPaths);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<DaemonManager>();
            builder.Services.AddSingleton<IBrowserAutomationBootstrapper, BrowserAutomationBootstrapper>();

            // Register DaemonClient, ChatNavigationState, and SessionConfig for ChatPage
            // (uses freshly-written config from the wizard's WriteConfig)
            builder.Services.AddSingleton(new ChatNavigationState());
            builder.Services.AddSingleton(sp => DaemonClientFactory.Create(
                sp.GetRequiredService<DaemonApi>().Endpoint,
                sp.GetRequiredService<NetclawPaths>()));
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
        var skillStatsMode = args.Length > 1 && string.Equals(args[1], "skills", StringComparison.OrdinalIgnoreCase);
        var optionStart = skillStatsMode ? 2 : 1;

        if (args.Length > optionStart && IsHelpToken(args[optionStart]))
        {
            WriteStatsHelp();
            return;
        }

        var statsAsJson = false;
        var statsTui = false;
        int? statsDays = null;
        for (var i = optionStart; i < args.Length; i++)
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
        var exitCode = skillStatsMode
            ? await RunSkillStatsAsync(scope.ServiceProvider, statsAsJson, statsDays)
            : await RunStatsAsync(scope.ServiceProvider, statsAsJson, statsDays);
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

            case "pair":
            {
                if (args.Length > 2 && IsHelpToken(args[2]))
                {
                    WriteDaemonPairHelp();
                    return;
                }

                var pairBuilder = Host.CreateApplicationBuilder(args);
                ConfigureConfigServices(pairBuilder.Services, pairBuilder.Configuration);
                pairBuilder.Logging.ClearProviders();
                pairBuilder.Logging.SetMinimumLevel(LogLevel.Warning);

                using var pairHost = pairBuilder.Build();
                var pairApi = pairHost.Services.GetRequiredService<DaemonApi>();
                var pairHubUrl = $"{pairApi.Endpoint}/hub/session";

                await using var pairConn = new HubConnectionBuilder()
                    .WithUrl(pairHubUrl)
                    .Build();

                try
                {
                    await pairConn.StartAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: Could not connect to daemon at {pairApi.Endpoint}: {ex.Message}");
                    Console.Error.WriteLine("Ensure the daemon is running: netclaw daemon start");
                    Environment.ExitCode = 1;
                    return;
                }

                try
                {
                    var pairingResult = await pairConn.InvokeAsync<PairingCodeResultDto>("GeneratePairingCode");
                    Console.WriteLine($"Pairing code:  {pairingResult.FormattedCode}");
                    Console.WriteLine($"Expires at:    {pairingResult.ExpiresAt.ToLocalTime():HH:mm:ss} (local time)");
                    Console.WriteLine();
                    Console.WriteLine("On the remote device, run:");
                    Console.WriteLine($"  netclaw pair {pairApi.Endpoint}");
                }
                catch (HubException ex)
                {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    Environment.ExitCode = 1;
                }

                return;
            }

            case "devices":
            {
                var devicesSubcmd = args.Length > 2 ? args[2] : "list";
                if (IsHelpToken(devicesSubcmd))
                {
                    WriteDaemonDevicesHelp();
                    return;
                }

                var devBuilder = Host.CreateApplicationBuilder(args);
                ConfigureConfigServices(devBuilder.Services, devBuilder.Configuration);
                devBuilder.Logging.ClearProviders();
                devBuilder.Logging.SetMinimumLevel(LogLevel.Warning);

                using var devHost = devBuilder.Build();
                var devApi = devHost.Services.GetRequiredService<DaemonApi>();

                if (devicesSubcmd is "revoke")
                {
                    var deviceName = args.Length > 3 ? args[3] : null;
                    if (string.IsNullOrWhiteSpace(deviceName))
                    {
                        Console.Error.WriteLine("error: device name required.");
                        Console.Error.WriteLine("Usage: netclaw daemon devices revoke <name>");
                        Environment.ExitCode = 1;
                        return;
                    }

                    try
                    {
                        var removed = await devApi.RevokePairedDeviceAsync(deviceName);
                        if (removed)
                            Console.WriteLine($"Device '{deviceName}' revoked.");
                        else
                        {
                            Console.Error.WriteLine($"Device '{deviceName}' not found.");
                            Environment.ExitCode = 1;
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.Error.WriteLine($"error: Could not reach daemon: {ex.Message}");
                        Environment.ExitCode = 1;
                    }
                }
                else
                {
                    // Default: list devices
                    try
                    {
                        var devices = await devApi.ListPairedDevicesAsync();
                        if (devices.Count == 0)
                        {
                            Console.WriteLine("No paired devices.");
                        }
                        else
                        {
                            Console.WriteLine($"{"Name",-24} {"Created",-22} {"Last Used",-22}");
                            Console.WriteLine(new string('-', 70));
                            foreach (var d in devices)
                            {
                                Console.WriteLine(
                                    $"{d.Name,-24} {d.CreatedAt.ToLocalTime(),-22:yyyy-MM-dd HH:mm} {d.LastUsedAt.ToLocalTime(),-22:yyyy-MM-dd HH:mm}");
                            }
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.Error.WriteLine($"error: Could not reach daemon: {ex.Message}");
                        Environment.ExitCode = 1;
                    }
                }

                return;
            }

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

        if ((mcpSubcommand is "tools" or "permissions") && args.Length <= 2)
        {
            // Bare `netclaw mcp tools` or `netclaw mcp permissions` → TUI mode
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

        if (mcpSubcommand is "auth" or "list" or "tools" or "permissions")
        {
            // auth/list/tools/permissions need the daemon — spin up DI to get DaemonApi
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

    // ── Skill management ──
    if (mode is "skill")
    {
        var paths = new NetclawPaths();
        paths.EnsureDirectoriesExist();
        // All skill subcommands are offline — no daemon needed
        Environment.ExitCode = await SkillCommand.RunAsync(args, paths);
        return;
    }

    // ── Webhook management ──
    if (mode is "webhooks")
    {
        var paths = new NetclawPaths();
        paths.EnsureDirectoriesExist();
        Environment.ExitCode = await WebhooksCommand.RunAsync(args, paths);
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

    // ── Device pairing (remote daemon) ──
    if (mode is "pair")
    {
        var pairPaths = new NetclawPaths();
        pairPaths.EnsureDirectoriesExist();
        Environment.ExitCode = await PairCommand.RunAsync(args, pairPaths);
        return;
    }

    // ── Self-update ──
    if (mode is "update")
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureConfigServices(builder.Services, builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        using var host = builder.Build();
        var paths = host.Services.GetRequiredService<NetclawPaths>();
        var daemonConfig = host.Services.GetRequiredService<DaemonConfig>();
        Environment.ExitCode = await UpdateCommand.RunAsync(args, paths, daemonConfig.DisableSelfUpdate);
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

    // ── Parse chat flags: --resume, -p/--prompt, --json ──
    string? resumeSessionId = null;
    bool chatJsonOutput = false;
    if (mode is "chat")
    {
        bool chatHeadless = false;
        string? chatPrompt = null;

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

            if (args[i] is "-p" or "--prompt")
            {
                chatHeadless = true;
                continue;
            }

            if (args[i] is "--json")
            {
                chatJsonOutput = true;
                continue;
            }

            if (IsHelpToken(args[i]))
            {
                WriteChatHelp();
                return;
            }

            // Positional argument: prompt text (when -p is specified)
            if (chatPrompt is null)
            {
                chatPrompt = args[i];
            }
        }

        if (chatHeadless)
        {
            if (chatPrompt is null)
            {
                Console.Error.WriteLine("netclaw: chat -p requires a prompt argument.");
                Console.Error.WriteLine("Usage: netclaw chat -p \"your prompt here\"");
                WriteChatHelp();
                Environment.ExitCode = 1;
                return;
            }

            headlessPrompt = chatPrompt;
            mode = "headless";
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
            var headlessOpts = new HeadlessOptions
            {
                Prompt = headlessPrompt!,
                ResumeSessionId = resumeSessionId,
                JsonOutput = chatJsonOutput,
            };
            webBuilder.Services.AddSingleton<HeadlessChannel>(sp =>
                ActivatorUtilities.CreateInstance<HeadlessChannel>(sp, headlessOpts));
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

static bool IsHelpToken(string token) => CliArgsParser.IsHelpToken(token);

static void WriteGeneralHelp()
{
    Console.WriteLine("Usage: netclaw <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  chat                     Interactive TUI chat");
    Console.WriteLine("  chat --resume <id>       Resume an existing session by ID");
    Console.WriteLine("  chat -p <text>           Headless single-prompt mode (supports --resume, --json)");
    Console.WriteLine("  sessions                 Browse and resume recent sessions (TUI)");
    Console.WriteLine("  sessions --once          List sessions and exit (no TUI, plain text or JSON)");
    Console.WriteLine("  doctor                   Configuration diagnostics (offline)");
    Console.WriteLine("  status                   Runtime status from daemon health JSON endpoint");
    Console.WriteLine("  stats                    Usage activity statistics from daemon");
    Console.WriteLine("  daemon <subcommand>      Manage daemon lifecycle and paired devices");
    Console.WriteLine("  pair <endpoint>          Pair this device with a remote daemon");
    Console.WriteLine("  mcp                      Manage MCP server profiles");
    Console.WriteLine("  provider                 Manage LLM providers (TUI) or use subcommands");
    Console.WriteLine("  model                    Manage model assignments (TUI) or use subcommands");
    Console.WriteLine("  reminder                 Manage scheduled reminders (daemon-required)");
    Console.WriteLine("  skill                    Manage skills and skill sources");
    Console.WriteLine("  webhooks                 Manage inbound webhook routes");
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
    Console.WriteLine("  start                        Start daemon as a background process");
    Console.WriteLine("  stop                         Stop daemon gracefully");
    Console.WriteLine("  status                       Show daemon process status");
    Console.WriteLine("  install                      Install systemd user service (Linux)");
    Console.WriteLine("  uninstall                    Remove systemd user service (Linux)");
    Console.WriteLine("  pair                         Generate a pairing code for remote device access");
    Console.WriteLine("  devices                      List paired devices");
    Console.WriteLine("  devices revoke <name>        Revoke a paired device by name");
}

static void WriteDaemonPairHelp()
{
    Console.WriteLine("Usage: netclaw daemon pair");
    Console.WriteLine();
    Console.WriteLine("Generate a pairing code so a remote device can authenticate with this daemon.");
    Console.WriteLine();
    Console.WriteLine("The code expires in 5 minutes. Share it with the remote device operator,");
    Console.WriteLine("who should run:  netclaw pair <endpoint>");
    Console.WriteLine();
    Console.WriteLine("Requires the daemon to be running and the command to be executed from the daemon host.");
}

static void WriteDaemonDevicesHelp()
{
    Console.WriteLine("Usage: netclaw daemon devices [revoke <name>]");
    Console.WriteLine();
    Console.WriteLine("Manage devices that have been paired with this daemon.");
    Console.WriteLine();
    Console.WriteLine("Subcommands:");
    Console.WriteLine("  (none)              List all paired devices with name, created, last-used");
    Console.WriteLine("  revoke <name>       Revoke a device token by device name");
    Console.WriteLine();
    Console.WriteLine("After revoking, the device will receive 401 on next connection attempt.");
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
    Console.WriteLine("Usage: netclaw chat [options] [prompt]");
    Console.WriteLine();
    Console.WriteLine("Start an interactive TUI chat session, or send a headless prompt.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --resume, -r <id>   Resume (or create) a session by ID");
    Console.WriteLine("  -p, --prompt        Send a single headless prompt (non-interactive)");
    Console.WriteLine("  --json              Output structured JSON (headless mode only)");
    Console.WriteLine("                      Includes sessionId, response, toolCalls, and usage");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  netclaw chat                                       Interactive TUI");
    Console.WriteLine("  netclaw chat --resume abc123                       Resume session in TUI");
    Console.WriteLine("  netclaw chat -p \"hello\"                            Headless single prompt");
    Console.WriteLine("  netclaw chat -p --resume my-session \"hello\"        Named session, headless");
    Console.WriteLine("  netclaw chat -p --resume my-session --json \"hello\" JSON output, named session");
    Console.WriteLine();
    Console.WriteLine("Use `netclaw sessions` to browse available sessions.");
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
    Console.WriteLine("       netclaw stats skills [options]");
    Console.WriteLine();
    Console.WriteLine("Shows usage activity statistics from the running daemon:");
    Console.WriteLine("  - token consumption (input, output)");
    Console.WriteLine("  - session and turn counts");
    Console.WriteLine("  - memory formation and recall counts");
    Console.WriteLine("  - skill load counts");
    Console.WriteLine("  - memory store statistics");
    Console.WriteLine("  - Slack activity counters");
    Console.WriteLine("  - webhook route counts and delivery counters");
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

    Console.WriteLine(JsonSerializer.Serialize(payload, JsonDefaults.Indented));
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
            var node = JsonSerializer.SerializeToNode(status, JsonDefaults.Api)!;
            var updateNode = (node["update"] as JsonObject) ?? [];
            var updateAvailable = string.Equals(cliUpdate.State, "update-available", StringComparison.Ordinal);
            updateNode["available"] = updateAvailable;
            updateNode["state"] = cliUpdate.State;
            updateNode["currentVersion"] = cliUpdate.CurrentVersion;
            updateNode["latestVersion"] = updateAvailable ? cliUpdate.LatestVersion : null;
            updateNode["releaseNotesUrl"] = updateAvailable ? cliUpdate.ReleaseNotesUrl : null;
            node["update"] = updateNode;
            Console.WriteLine(node.ToJsonString(JsonDefaults.Indented));
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
            Console.WriteLine(JsonSerializer.Serialize(sessions, JsonDefaults.Indented));
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

    foreach (var channel in status.Telemetry.Channels)
    {
        Console.WriteLine(
            $"{channel.ChannelType} counters: recv={channel.EventsReceived} routed={channel.EventsRouted} dropped={channel.EventsDropped} replied={channel.RepliesPosted} rejected={channel.RepliesRejected} reply_failed={channel.RepliesFailed}");
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
            Console.WriteLine((status.Update?.SelfUpdateDisabled).GetValueOrDefault()
                ? "  Pull a newer container image to upgrade."
                : "  Run: netclaw update");
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
            Console.WriteLine(JsonSerializer.Serialize(stats, JsonDefaults.IndentedCamelCase));
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

static async Task<int> RunSkillStatsAsync(IServiceProvider services, bool jsonOutput, int? days = null)
{
    var api = services.GetRequiredService<DaemonApi>();

    try
    {
        var stats = await api.GetSkillUsageStatsAsync(days);

        if (stats is null)
        {
            Console.WriteLine("[FAIL] stats skills: daemon returned an empty stats payload.");
            return 1;
        }

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(stats, JsonDefaults.IndentedCamelCase));
        }
        else
        {
            WriteSkillStatsResult(stats, days);
        }

        return 0;
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        Console.WriteLine($"[FAIL] stats skills: daemon returned {(int)ex.StatusCode} from {api.Endpoint}");
        Console.WriteLine("       fix: run `netclaw daemon start` and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] stats skills: unable to reach daemon at {api.Endpoint}: {ex.Message}");
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

    foreach (var channel in stats.Channels)
    {
        Console.WriteLine($"{channel.ChannelType}:");
        Console.WriteLine($"  events: recv={channel.EventsReceived} routed={channel.EventsRouted} dropped={channel.EventsDropped}");
        Console.WriteLine($"  replies: posted={channel.RepliesPosted} rejected={channel.RepliesRejected} failed={channel.RepliesFailed}");
        if (channel.Extras is { Count: > 0 })
        {
            var extras = string.Join(" ", channel.Extras.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"  extras: {extras}");
        }
        Console.WriteLine();
    }

    Console.WriteLine("webhooks:");
    Console.WriteLine($"  routes: total={stats.Webhooks.TotalRoutes} enabled={stats.Webhooks.EnabledRoutes} disabled={stats.Webhooks.DisabledRoutes} invalid={stats.Webhooks.InvalidRoutes}");
    Console.WriteLine($"  deliveries: accepted={stats.Webhooks.Accepted} filtered={stats.Webhooks.EventFiltered} duplicate={stats.Webhooks.DuplicateDelivery}");
    Console.WriteLine($"  rejected: 404={stats.Webhooks.RouteNotFound} 401={stats.Webhooks.VerificationFailed} 413={stats.Webhooks.BodyTooLarge} 400={stats.Webhooks.InvalidJson} 429={stats.Webhooks.RateLimited}");

    if (stats.Reminders is { } reminders)
    {
        Console.WriteLine();
        Console.WriteLine("reminders:");
        Console.WriteLine($"  scheduled: {reminders.ScheduledCount}    active: {reminders.ActiveExecutions}    failed: {reminders.FailedCount}");
    }
}

static void WriteSkillStatsResult(SkillUsageStats.Response stats, int? days)
{
    if (days is > 0)
        Console.WriteLine($"netclaw stats skills — last {days} days");
    else if (days == 0)
        Console.WriteLine("netclaw stats skills — all time");
    else
        Console.WriteLine("netclaw stats skills — last 7 days");

    Console.WriteLine();

    if (stats.Daily.Count == 0)
    {
        Console.WriteLine("No skill loads recorded.");
        return;
    }

    foreach (var day in stats.Daily)
    {
        Console.WriteLine($"{day.Date}  total={day.TotalLoads:N0}");
        Console.WriteLine($"  by method: {string.Join(", ", day.Methods.Select(m => $"{m.Method}={m.Count:N0}"))}");

        foreach (var skill in day.Skills.Take(15))
        {
            Console.WriteLine($"  {skill.SkillName}: {skill.TotalLoads:N0} ({string.Join(", ", skill.Methods.Select(m => $"{m.Method}={m.Count:N0}"))})");
        }

        if (day.Skills.Count > 15)
            Console.WriteLine($"  ... {day.Skills.Count - 15} more skill(s)");

        Console.WriteLine();
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

    // Layered daemon/operator configuration chain:
    // 1. netclaw.json (daemon-owned config, optional)
    // 2. secrets.json (credentials overlay, optional)
    // 3. NETCLAW_* environment variables (highest priority)
    configuration
        .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
        .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("NETCLAW_");

    services.AddSingleton(DaemonConfig.BindFromConfiguration(configuration.GetSection("Daemon")));

    // TimeProvider (virtualized for testing)
    services.AddSingleton(TimeProvider.System);

    // Shared daemon HTTP API client — single endpoint resolution for all commands
    services.AddHttpClient();
    services.AddSingleton<DaemonApi>();

    return paths;
}

static IConfigurationRoot BuildCliConfig()
{
    var paths = new NetclawPaths();
    paths.EnsureDirectoriesExist();

    return new ConfigurationBuilder()
        .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
        .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("NETCLAW_")
        .Build();
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
    services.AddSingleton(sp =>
    {
        var contextWindow = models.Main.ContextWindow;
        if (contextWindow is null)
        {
            var daemon = sp.GetRequiredService<DaemonApi>();
            var status = daemon.GetStatusAsync().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException(
                    "Daemon returned empty status. Cannot resolve effective context window. " +
                    "Set Models.Main.ContextWindow in netclaw.json or ensure the daemon is healthy.");
            contextWindow = status.Model?.ContextWindow is > 0 and var daemonCw
                ? daemonCw
                : throw new InvalidOperationException(
                    $"Daemon reported no context window for model '{models.Main.ModelId}'. " +
                    "Set Models.Main.ContextWindow in netclaw.json.");
        }

        return new ModelCapabilities
        {
            ModelId = models.Main.ModelId,
            ContextWindowTokens = contextWindow.Value,
            CompactionModelId = models.Compaction?.ModelId,
        };
    });

    // DaemonClient uses the endpoint from DaemonApi. For non-loopback (remote) endpoints,
    // reads DeviceToken from secrets.json and attaches it as a bearer token provider.
    services.AddSingleton(sp => DaemonClientFactory.Create(
        sp.GetRequiredService<DaemonApi>().Endpoint,
        sp.GetRequiredService<NetclawPaths>()));
}
