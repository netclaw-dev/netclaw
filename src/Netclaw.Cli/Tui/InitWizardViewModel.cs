using System.Text.Json;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Wizard steps for the init flow.
/// </summary>
public enum WizardStep
{
    Provider = 1,
    ChatServices = 2,
    Acl = 3,
    Search = 4,
    BrowserAutomation = 5,
    Exposure = 6,
    Identity = 7,
    HealthCheck = 8
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw init</c> onboarding wizard.
/// Drives an onboarding wizard state machine with back-navigation support.
/// ACL step is conditionally skipped when no chat services are enabled.
/// </summary>
public partial class InitWizardViewModel : ReactiveViewModel
{
    public const int TotalSteps = 8;

    private readonly NetclawPaths _paths;
    private readonly IProviderProbe _probe;
    private readonly ProviderDescriptorRegistry _registry;
    private readonly ISlackProbe _slackProbe;
    private readonly IBrowserAutomationBootstrapper _browserBootstrapper;

    /// <summary>
    /// The provider descriptor registry. Exposed for use by the page.
    /// </summary>
    public ProviderDescriptorRegistry Registry => _registry;
    private readonly DaemonManager? _daemonManager;
    private readonly string _daemonEndpoint;
    private CancellationTokenSource? _probeCts;

    public ReactiveProperty<WizardStep> CurrentStep { get; } = new(WizardStep.Provider);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<bool> IsHealthCheckRunning { get; } = new(false);
    public ReactiveProperty<bool> IsComplete { get; } = new(false);
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);

    /// <summary>
    /// Elapsed seconds since the current probe started. Ticks once per second
    /// while <see cref="IsProbing"/> is true. The page uses this to animate the
    /// validation spinner and show a live timer.
    /// </summary>
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);

    /// <summary>
    /// Monotonically increasing counter that ticks whenever health check results
    /// change. The page subscribes to this to invalidate its DynamicLayoutNode,
    /// since RequestRedraw alone won't trigger factory re-evaluation in Termina 0.7.1+.
    /// </summary>
    internal ReactiveProperty<int> HealthCheckResultVersion { get; } = new(0);

    // ── Step 1: Provider ──
    public string? SelectedProviderType { get; set; }
    public AuthMethod SelectedAuthMethod { get; set; } = AuthMethod.None;
    public string? ApiKeyInput { get; set; }
    public string? EndpointInput { get; set; }

    // ── Step 1 (continued): Model selection ──
    public string? SelectedModelId { get; set; }
    public List<DiscoveredModel> DiscoveredModels { get; } = [];

    // ── Step 2: Chat Services ──
    public string? SlackBotToken { get; set; }
    public string? SlackAppToken { get; set; }
    public bool SlackEnabled { get; set; }
    public string? SlackChannelNamesInput { get; set; }
    public bool SlackAllowDirectMessages { get; set; }
    public string? SlackAllowedUserIdsInput { get; set; }
    internal SlackChannelResolutionResult? LastChannelResolution { get; private set; }

    // ── Step 3: ACL ──
    public string? OwnerIdentity { get; set; }

    // ── Step 4: Search ──
    public string SelectedSearchBackend { get; set; } = "duckduckgo";
    public string? BraveApiKeyInput { get; set; }
    public string? SearXngEndpointInput { get; set; }

    // ── Step 5: Browser automation ──
    public bool BrowserAutomationEnabled { get; set; }
    public string SelectedBrowserAutomationBackend { get; set; } = BrowserAutomationMcpProfiles.ChromeDevToolsBackend;

    // ── Step 6: Exposure ──
    public string? ExposureMode { get; set; }

    // ── Step 7: Identity ──
    public string AgentName { get; set; } = "Netclaw";
    public string? CommunicationStyle { get; set; }
    public string? UserName { get; set; }
    public string UserTimezone { get; set; } = TimeZoneInfo.Local.Id;
    public string? PrimaryUse { get; set; }

    // ── Step 8: Health Check ──
    public List<HealthCheckItem> HealthCheckResults { get; } = [];

    /// <summary>
    /// Completes when the health check finishes. Used for testing without polling.
    /// </summary>
    internal Task? HealthCheckCompletion { get; private set; }

    /// <summary>
    /// Completes when the provider probe finishes. Used for testing without polling.
    /// </summary>
    internal Task? ProbeCompletion { get; private set; }

    public InitWizardViewModel(
        NetclawPaths paths,
        ProviderDescriptorRegistry registry,
        ISlackProbe slackProbe,
        DaemonManager? daemonManager = null,
        string? daemonEndpoint = null)
        : this(paths, registry, registry, slackProbe, null, daemonManager, daemonEndpoint)
    {
    }

    /// <summary>
    /// Test constructor allowing a separate probe implementation from the registry.
    /// </summary>
    internal InitWizardViewModel(
        NetclawPaths paths,
        ProviderDescriptorRegistry registry,
        IProviderProbe probe,
        ISlackProbe slackProbe,
        IBrowserAutomationBootstrapper? browserBootstrapper = null,
        DaemonManager? daemonManager = null,
        string? daemonEndpoint = null)
    {
        _paths = paths;
        _probe = probe;
        _registry = registry;
        _slackProbe = slackProbe;
        _browserBootstrapper = browserBootstrapper ?? new BrowserAutomationBootstrapper();
        _daemonManager = daemonManager;
        _daemonEndpoint = daemonEndpoint ?? "http://127.0.0.1:5199";
    }

    public override void OnActivated()
    {
        base.OnActivated();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleGlobalKey)
            .DisposeWith(Subscriptions);
    }

    /// <summary>
    /// Returns true if any chat service is enabled (extensible for future services).
    /// </summary>
    public bool AnyChatServicesEnabled() => SlackEnabled;

    /// <summary>
    /// Returns the number of active steps (skipping ACL when no chat services).
    /// </summary>
    public int ActiveStepCount => AnyChatServicesEnabled() ? TotalSteps : TotalSteps - 1;

    /// <summary>
    /// Returns the display number for the given step, accounting for skipped steps.
    /// </summary>
    public int GetDisplayStepNumber(WizardStep step)
    {
        var num = (int)step;
        if (!AnyChatServicesEnabled() && step > WizardStep.ChatServices)
            return num - 1;
        return num;
    }

    /// <summary>
    /// Advance to the next wizard step, or write config on the final step.
    /// Skips ACL when no chat services are enabled.
    /// </summary>
    public void GoNext()
    {
        if (CurrentStep.Value == WizardStep.HealthCheck)
        {
            if (!IsHealthCheckRunning.Value && !IsComplete.Value)
                HealthCheckCompletion = RunHealthCheckAsync();
            return;
        }

        var next = (WizardStep)((int)CurrentStep.Value + 1);

        // Skip ACL when no chat services are enabled
        if (next == WizardStep.Acl && !AnyChatServicesEnabled())
            next = (WizardStep)((int)next + 1);

        CurrentStep.Value = next;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    /// <summary>
    /// Go back one step. Clears downstream state per the design doc's
    /// back-navigation clearing rules. Skips ACL when no chat services.
    /// </summary>
    public void GoBack()
    {
        if (CurrentStep.Value == WizardStep.Provider)
        {
            // Can't go back from step 1 — quit
            Shutdown();
            return;
        }

        var previous = (WizardStep)((int)CurrentStep.Value - 1);

        // Skip ACL when going back too
        if (previous == WizardStep.Acl && !AnyChatServicesEnabled())
            previous = (WizardStep)((int)previous - 1);

        // Back-navigation clearing rules from design doc:
        // Provider change clears auth + model downstream
        if (previous == WizardStep.Provider)
        {
            ClearFromProvider();
        }

        CurrentStep.Value = previous;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void RequestQuit()
    {
        Shutdown();
    }

    /// <summary>
    /// Probe the provider to validate credentials and discover models.
    /// Cancels any in-flight probe before starting a new one.
    /// </summary>
    public void StartProbe()
    {
        CancelProbe();
        ProbeCompletion = ProbeProviderAsync();
    }

    /// <summary>
    /// Cancel any in-flight probe (e.g., on back-navigation).
    /// </summary>
    public void CancelProbe()
    {
        if (_probeCts is not null)
        {
            _probeCts.Cancel();
            _probeCts.Dispose();
            _probeCts = null;
        }
    }

    internal async Task ProbeProviderAsync()
    {
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        IsProbing.Value = true;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        RequestRedraw();

        // Fire-and-forget timer — self-cancels via the shared CTS.
        // RunProbeTimerAsync handles OperationCanceledException internally
        // and exits cleanly, so no need to await it after cancellation.
        _ = RunProbeTimerAsync(ct);

        var result = await _probe.ProbeAsync(
            SelectedProviderType ?? "unknown",
            EndpointInput,
            ApiKeyInput,
            ct);

        // Stop the timer
        CancelProbe();

        DiscoveredModels.Clear();
        if (result.Success)
            DiscoveredModels.AddRange(result.Models);

        ProbeResult.Value = result;
        IsProbing.Value = false;
        RequestRedraw();
    }

    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ProbeElapsedSeconds.Value++;
            RequestRedraw();
        }
    }

    private void HandleGlobalKey(KeyPressed key)
    {
        if (key.KeyInfo.Key == ConsoleKey.Q &&
            key.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Shutdown();
        }
    }

    internal void ClearFromProvider()
    {
        CancelProbe();
        SelectedAuthMethod = AuthMethod.None;
        ApiKeyInput = null;
        EndpointInput = null;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        SelectedModelId = null;
        DiscoveredModels.Clear();
    }

    private static IReadOnlyList<string> ParseChannelNames(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.TrimStart('#').Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

    private static IReadOnlyList<string> ParseUserIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

    private async Task RunHealthCheckAsync()
    {
        IsHealthCheckRunning.Value = true;
        IsComplete.Value = false;
        HealthCheckResults.Clear();
        NotifyHealthCheckChanged();

        // Provider check
        HealthCheckResults.Add(new HealthCheckItem("LLM provider configured", null));
        NotifyHealthCheckChanged();
        await Task.Delay(200); // simulate validation

        var providerOk = !string.IsNullOrWhiteSpace(SelectedProviderType);
        HealthCheckResults[^1] = new HealthCheckItem(
            $"LLM provider configured ({SelectedProviderType ?? "none"})",
            providerOk);
        NotifyHealthCheckChanged();

        // Model check
        HealthCheckResults.Add(new HealthCheckItem("Model selected", null));
        NotifyHealthCheckChanged();
        await Task.Delay(200);

        var modelOk = !string.IsNullOrWhiteSpace(SelectedModelId);
        HealthCheckResults[^1] = new HealthCheckItem(
            modelOk
                ? $"Model selected ({SelectedModelId})"
                : "Model selected (none — will use provider default)",
            true); // not a hard failure
        NotifyHealthCheckChanged();

        // Slack check
        HealthCheckResults.Add(new HealthCheckItem("Slack configuration", null));
        NotifyHealthCheckChanged();

        var slackAuthOk = false;
        if (!SlackEnabled)
        {
            HealthCheckResults[^1] = new HealthCheckItem(
                "Slack configuration (disabled)", true);
        }
        else if (string.IsNullOrWhiteSpace(SlackBotToken))
        {
            HealthCheckResults[^1] = new HealthCheckItem(
                "Slack configuration (bot token missing)", false);
        }
        else
        {
            var slackResult = await _slackProbe.ProbeAsync(SlackBotToken!);
            if (slackResult.Success)
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Slack bot authenticated (team: {slackResult.TeamName})", true);
                slackAuthOk = true;
            }
            else
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Slack auth failed: {slackResult.ErrorMessage}", false);
            }
        }
        NotifyHealthCheckChanged();

        // Channel resolution — only when auth succeeded and names were provided
        var parsedChannelNames = ParseChannelNames(SlackChannelNamesInput);
        if (slackAuthOk && parsedChannelNames.Count > 0)
        {
            HealthCheckResults.Add(new HealthCheckItem("Resolving Slack channels", null));
            NotifyHealthCheckChanged();

            LastChannelResolution = await _slackProbe.ResolveChannelNamesAsync(
                SlackBotToken!, parsedChannelNames);

            if (LastChannelResolution.ErrorMessage is not null)
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Slack channel lookup failed: {LastChannelResolution.ErrorMessage}", false);
            }
            else if (LastChannelResolution.Unresolved.Count > 0)
            {
                var notFound = string.Join(", ", LastChannelResolution.Unresolved.Select(n => $"#{n}"));
                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Slack channels: resolved {LastChannelResolution.Resolved.Count}/{parsedChannelNames.Count}, not found: {notFound}",
                    false);
            }
            else
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Slack channels resolved ({LastChannelResolution.Resolved.Count})", true);
            }
            NotifyHealthCheckChanged();
        }

        // Browser automation prerequisites (optional)
        if (BrowserAutomationEnabled)
        {
            HealthCheckResults.Add(new HealthCheckItem("Browser automation prerequisites", null));
            NotifyHealthCheckChanged();

            var bootstrap = await _browserBootstrapper.EnsureReadyAsync(SelectedBrowserAutomationBackend);
            if (bootstrap.Success)
            {
                var backendName = SelectedBrowserAutomationBackend == BrowserAutomationMcpProfiles.PlaywrightBackend
                    ? "Playwright MCP"
                    : "Chrome DevTools MCP";
                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Browser automation ready ({backendName})", true);
                NotifyHealthCheckChanged();
            }
            else
            {
                var suffix = string.IsNullOrWhiteSpace(bootstrap.ManualCommand)
                    ? string.Empty
                    : $" Command: {bootstrap.ManualCommand}";

                HealthCheckResults[^1] = new HealthCheckItem(
                    $"Browser automation setup blocked: {bootstrap.Message}{suffix}", false);
                NotifyHealthCheckChanged();

                if (bootstrap.NeedsManualAction)
                {
                    IsHealthCheckRunning.Value = false;
                    StatusMessage.Value = string.IsNullOrWhiteSpace(bootstrap.ManualCommand)
                        ? $"{bootstrap.Message} Press Enter to retry."
                        : $"{bootstrap.Message} Run `{bootstrap.ManualCommand}`, then press Enter to retry.";
                    return;
                }
            }
        }

        // Config write
        HealthCheckResults.Add(new HealthCheckItem("Writing configuration", null));
        NotifyHealthCheckChanged();

        try
        {
            WriteConfig();
            HealthCheckResults[^1] = new HealthCheckItem("Configuration written", true);
        }
        catch (Exception ex)
        {
            HealthCheckResults[^1] = new HealthCheckItem(
                $"Configuration write failed: {ex.Message}", false);
        }

        // Check if all health checks passed so far — only start daemon if config is clean
        var allPassed = HealthCheckResults.All(h => h.Passed == true);
        if (allPassed)
        {
            // Start daemon and verify it's healthy before navigating to chat
            HealthCheckResults.Add(new HealthCheckItem("Starting daemon", null));
            NotifyHealthCheckChanged();

            var daemonOk = await StartAndPollDaemonAsync();
            if (daemonOk)
            {
                HealthCheckResults[^1] = new HealthCheckItem("Daemon ready", true);
            }
            else
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    "Daemon did not become ready (personality setup skipped)", false);
            }
            NotifyHealthCheckChanged();
        }

        IsHealthCheckRunning.Value = false;
        IsComplete.Value = true;
        NotifyHealthCheckChanged();

        // Navigate to chat if everything is healthy (including daemon)
        allPassed = HealthCheckResults.All(h => h.Passed == true);
        if (allPassed)
        {
            StatusMessage.Value = "Setup complete! Launching chat...";
            Navigate?.Invoke("/chat");
        }
        else
        {
            StatusMessage.Value = "Setup complete with warnings. Run `netclaw daemon start` to begin.";
        }
    }

    /// <summary>
    /// Start the daemon and poll its health endpoint until ready (up to 30s).
    /// Returns false if DaemonManager is not available or health poll times out.
    /// </summary>
    private async Task<bool> StartAndPollDaemonAsync()
    {
        if (_daemonManager is null)
            return false;

        var result = _daemonManager.Start();
        if (!result.Success && !result.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
            return false;

        using var httpClient = new HttpClient();
        var healthUrl = $"{_daemonEndpoint}/api/health/ready";

        for (var i = 0; i < 30; i++)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException)
            {
                HealthCheckResults[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyHealthCheckChanged();
            }
            catch (TaskCanceledException)
            {
                HealthCheckResults[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyHealthCheckChanged();
            }

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    /// Bumps the health check version counter and requests a redraw.
    /// The page subscribes to HealthCheckResultVersion to invalidate its
    /// DynamicLayoutNode, which won't rebuild from RequestRedraw alone.
    /// </summary>
    private void NotifyHealthCheckChanged()
    {
        HealthCheckResultVersion.Value++;
        RequestRedraw();
    }

    private void WriteConfig()
    {
        _paths.EnsureDirectoriesExist();

        // Build netclaw.json (non-secret settings)
        var config = new Dictionary<string, object>
        {
            ["configVersion"] = 1
        };

        // Provider section
        var providers = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(SelectedProviderType))
        {
            var providerName = SelectedProviderType!.ToLowerInvariant();
            var providerEntry = new Dictionary<string, object>
            {
                ["Type"] = providerName
            };

            if (SelectedAuthMethod != AuthMethod.None)
                providerEntry["AuthMethod"] = SelectedAuthMethod.ToString();

            if (!string.IsNullOrWhiteSpace(EndpointInput))
                providerEntry["Endpoint"] = EndpointInput;
            else if (_registry.TryGet(providerName, out var desc)
                     && desc.CredentialMode == CredentialInputMode.EndpointOnly)
                providerEntry["Endpoint"] = desc.DefaultEndpoint;

            providers[providerName] = providerEntry;
        }

        if (providers.Count > 0)
            config["Providers"] = providers;

        // Models section — set the main model provider + model ID
        if (!string.IsNullOrWhiteSpace(SelectedProviderType))
        {
            var modelEntry = new Dictionary<string, object>
            {
                ["Provider"] = SelectedProviderType.ToLowerInvariant()
            };

            if (!string.IsNullOrWhiteSpace(SelectedModelId))
                modelEntry["ModelId"] = SelectedModelId;

            config["Models"] = new Dictionary<string, object>
            {
                ["Main"] = modelEntry
            };
        }

        // Slack section
        if (SlackEnabled)
        {
            var slackSection = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["SocketMode"] = true
            };

            if (LastChannelResolution is { Resolved.Count: > 0 })
            {
                var ids = LastChannelResolution.Resolved.Select(r => r.Id).ToArray();
                slackSection["AllowedChannelIds"] = ids;
                slackSection["DefaultChannelId"] = ids[0];
            }

            if (SlackAllowDirectMessages)
            {
                slackSection["AllowDirectMessages"] = true;
            }

            var userIds = ParseUserIds(SlackAllowedUserIdsInput);
            if (userIds.Count > 0)
            {
                slackSection["AllowedUserIds"] = userIds.ToArray();
            }

            config["Slack"] = slackSection;
        }

        // Search section
        if (SelectedSearchBackend != "duckduckgo")
        {
            var searchSection = new Dictionary<string, object>
            {
                ["Backend"] = SelectedSearchBackend
            };

            if (SelectedSearchBackend == "searxng" && !string.IsNullOrWhiteSpace(SearXngEndpointInput))
                searchSection["SearXngEndpoint"] = SearXngEndpointInput;

            config["Search"] = searchSection;
        }

        // Browser automation MCP profile (optional)
        if (BrowserAutomationEnabled)
        {
            var (profileName, entry) = BrowserAutomationMcpProfiles.Create(SelectedBrowserAutomationBackend);

            config["McpServers"] = new Dictionary<string, object>
            {
                [profileName] = new Dictionary<string, object?>
                {
                    ["Transport"] = entry.Transport,
                    ["Command"] = entry.Command,
                    ["Arguments"] = entry.Arguments,
                    ["Enabled"] = entry.Enabled,
                    ["GrantCategory"] = entry.GrantCategory
                }
            };
        }

        // Write identity files
        WriteIdentityFiles();

        // Write netclaw.json
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, jsonOptions));

        // Build secrets.json (sensitive values)
        var secrets = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(ApiKeyInput) && !string.IsNullOrWhiteSpace(SelectedProviderType))
        {
            secrets["Providers"] = new Dictionary<string, object>
            {
                [SelectedProviderType.ToLowerInvariant()] = new Dictionary<string, object>
                {
                    ["ApiKey"] = ApiKeyInput
                }
            };
        }

        if (SlackEnabled)
        {
            var slackSecrets = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(SlackBotToken))
                slackSecrets["BotToken"] = SlackBotToken;
            if (!string.IsNullOrWhiteSpace(SlackAppToken))
                slackSecrets["AppToken"] = SlackAppToken;
            if (slackSecrets.Count > 0)
                secrets["Slack"] = slackSecrets;
        }

        if (SelectedSearchBackend == "brave" && !string.IsNullOrWhiteSpace(BraveApiKeyInput))
        {
            secrets["Search"] = new Dictionary<string, object>
            {
                ["BraveApiKey"] = BraveApiKeyInput
            };
        }

        if (secrets.Count > 0)
        {
            File.WriteAllText(_paths.SecretsPath,
                JsonSerializer.Serialize(secrets, jsonOptions));
        }
    }

    internal void WriteIdentityFiles()
    {
        var styleDescription = CommunicationStyle switch
        {
            "Concise & casual" => "Be concise and casual. Keep responses short and conversational.",
            "Concise & formal" => "Be concise and formal. Keep responses brief and professional.",
            "Detailed & casual" => "Be detailed and casual. Give thorough explanations in a friendly tone.",
            "Detailed & formal" => "Be detailed and formal. Give thorough, professional explanations.",
            _ => "Be concise and casual. Keep responses short and conversational."
        };

        var name = AgentName;
        var userName = string.IsNullOrWhiteSpace(UserName) ? "User" : UserName;
        var timezone = UserTimezone;
        var primaryUse = string.IsNullOrWhiteSpace(PrimaryUse) ? "General purpose" : PrimaryUse;

        File.WriteAllText(_paths.SoulPath,
            $"""
            # {name}

            ## Communication Style
            {styleDescription}

            ## User
            - Name: {userName}
            - Timezone: {timezone}
            - Primary use: {primaryUse}
            """);

        File.WriteAllText(_paths.AgentsPath,
            $"""
            # Operating Rules

            - Ask before making destructive changes to files or infrastructure
            - Prefer concise tool usage — avoid unnecessary search_tools calls
            - For MCP capabilities, use progressive discovery: search_tools("servers") -> search_tools("<intent>", server: "<server_name>")
            - For interactive web tasks (clicking, typing, form filling), use browser MCP tools; do not substitute web_fetch/file_read/shell_execute for browser interaction
            - For browser automation, prefer file outputs over inline page dumps; avoid returning full DOM snapshots unless explicitly requested

            ## Identity Files

            Identity configuration lives in `{_paths.IdentityDirectory}/`:

            | File | Purpose |
            |------|---------|
            | `{_paths.SoulPath}` | Personality, tone, user profile |
            | `{_paths.AgentsPath}` | Operating rules, meta-guidance (this file) |
            | `{_paths.ToolingPath}` | Host environment capabilities |

            Update these files directly as you learn more about the user and their environment.

            ### Progressive Disclosure

            Keep top-level files concise — a quick summary the system prompt can load every turn.
            When a topic needs more depth, create a detail file in the matching subdirectory:

            - `{_paths.SoulDetailDirectory}/` — e.g., `communication-preferences.md`, `work-context.md`
            - `{_paths.AgentsDetailDirectory}/` — e.g., `tool-policies.md`, `safety-rules.md`
            - `{_paths.ToolingDetailDirectory}/` — e.g., `docker.md`, `kubernetes.md`
            - `{_paths.McpShadowDirectory}/` — system-generated MCP shadow catalogs (do not edit)

            The top-level file should reference detail files so they can be loaded on demand.
            """);

        File.WriteAllText(_paths.ToolingPath,
            """
            # Environment Capabilities

            No capabilities discovered yet. Run `netclaw doctor` or ask Netclaw to probe your environment.
            """);
    }

    public override void Dispose()
    {
        CancelProbe();
        CurrentStep.Dispose();
        StatusMessage.Dispose();
        IsHealthCheckRunning.Dispose();
        IsComplete.Dispose();
        IsProbing.Dispose();
        ProbeResult.Dispose();
        ProbeElapsedSeconds.Dispose();
        HealthCheckResultVersion.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Result of a single health check probe.
/// </summary>
/// <param name="Label">Display text.</param>
/// <param name="Passed">Null while running, true/false when complete.</param>
public sealed record HealthCheckItem(string Label, bool? Passed);
