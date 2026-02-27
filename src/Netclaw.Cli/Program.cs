using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Netclaw.Channels;
using Netclaw.Cli;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
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
    var mode = args.Length > 0 ? args[0] : "chat";
    string? headlessPrompt = null;

    if (IsHelpToken(mode))
    {
        WriteGeneralHelp();
        return;
    }

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
            builder.Services.AddDoctorChecks();

        // Suppress framework console logging
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        if (mode is "init")
        {
            // Enable Termina trace logging for debugging TUI input/rendering issues
            var traceFile = Path.Combine(Path.GetTempPath(), "netclaw-init-trace.log");
            builder.Services.AddTerminaFileTracing(traceFile, TerminaTraceCategory.All, TerminaTraceLevel.Trace);
            Console.Error.WriteLine($"Trace log: {traceFile}");

            // Provider probe for credential validation + model discovery
            builder.Services.AddHttpClient<IProviderProbe, ProviderProbe>();

            builder.Services.AddTermina("/init", termina =>
            {
                termina.RegisterRoute<InitWizardPage, InitWizardViewModel>("/init");
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
        builder.Services.AddHttpClient();

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var exitCode = await RunStatusAsync(scope.ServiceProvider, builder.Configuration, statusAsJson);
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
                var stopResult = await manager.StopAsync();
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

static bool IsHelpToken(string token)
{
    return token is "help" or "-h" or "--help";
}

static void WriteGeneralHelp()
{
    Console.WriteLine("Usage: netclaw <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  chat                     Interactive TUI chat (default)");
    Console.WriteLine("  -p, --prompt <text>      Headless single-prompt mode");
    Console.WriteLine("  doctor                   Configuration diagnostics (offline)");
    Console.WriteLine("  status                   Runtime status from daemon health JSON endpoint");
    Console.WriteLine("  daemon <subcommand>      Manage daemon lifecycle");
    Console.WriteLine("  init                     First-run setup wizard (planned)");
    Console.WriteLine("  config                   Configuration management (planned)");
    Console.WriteLine();
    Console.WriteLine("Run `netclaw daemon --help`, `netclaw doctor --help`, or `netclaw status --help` for details.");
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
    Console.WriteLine("Runs offline configuration diagnostics:");
    Console.WriteLine("  - netclaw.json schema validation (versioned by configVersion)");
    Console.WriteLine("  - secrets.json syntax validation");
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

static async Task<int> RunStatusAsync(IServiceProvider services, IConfiguration configuration, bool jsonOutput)
{
    var endpoint = configuration["Daemon:Endpoint"]
        ?? Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
        ?? "http://127.0.0.1:5199";

    var url = $"{endpoint.TrimEnd('/')}/api/health/status";
    var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
    var client = httpClientFactory.CreateClient();

    try
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync(url, timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[FAIL] status: daemon returned {(int)response.StatusCode} from {url}");
            Console.WriteLine("       fix: run `netclaw daemon status` and `netclaw daemon start`.");
            return 1;
        }

        var payload = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        var status = await JsonSerializer.DeserializeAsync<DaemonRuntimeStatusDto>(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            timeoutCts.Token);

        if (status is null)
        {
            Console.WriteLine("[FAIL] status: daemon returned an empty status payload.");
            return 1;
        }

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        else
        {
            WriteStatusResult(status, endpoint);
        }

        return status.Overall.ToLowerInvariant() switch
        {
            "healthy" => 0,
            "degraded" => 2,
            _ => 1
        };
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] status: unable to reach daemon at {url}: {ex.Message}");
        Console.WriteLine("       fix: run `netclaw daemon start` and retry.");
        return 1;
    }
}

static void WriteStatusResult(DaemonRuntimeStatusDto status, string endpoint)
{
    Console.WriteLine($"overall: {status.Overall}");
    Console.WriteLine($"daemon: PID {status.Process.Pid}, uptime {FormatUptime(status.Process.UptimeSeconds)}, endpoint {endpoint}");
    Console.WriteLine($"persistence: {status.Persistence.Provider}");
    Console.WriteLine($"telemetry: {(status.Telemetry.Enabled ? "enabled" : "disabled")}" +
                      (status.Telemetry.Enabled && !string.IsNullOrWhiteSpace(status.Telemetry.OtlpEndpoint)
                          ? $" ({status.Telemetry.OtlpEndpoint})"
                          : string.Empty));

    Console.WriteLine("connectors:");
    foreach (var connector in status.Connectors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
    {
        var enabled = connector.Enabled ? "enabled" : "disabled";
        Console.WriteLine($"- {connector.Key}: {connector.Status} ({enabled})");
        if (!string.IsNullOrWhiteSpace(connector.Message))
            Console.WriteLine($"    {connector.Message}");
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
