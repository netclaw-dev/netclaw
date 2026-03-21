using System.Diagnostics;
using System.Text.Json;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Configuration.Secrets;
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
    Memory = 6,
    Exposure = 7,
    Identity = 8,
    HealthCheck = 9
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw init</c> onboarding wizard.
/// Drives an onboarding wizard state machine with back-navigation support.
/// ACL step is conditionally skipped when no chat services are enabled.
/// </summary>
public partial class InitWizardViewModel : ReactiveViewModel
{
    public const int TotalSteps = 9;
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SlackProbeHardTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChannelResolutionHardTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan BrowserBootstrapHardTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DaemonPollRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OverallHealthCheckTimeout = TimeSpan.FromMinutes(5);

    private readonly NetclawPaths _paths;
    private readonly IProviderProbe _probe;
    private readonly ProviderDescriptorRegistry _registry;
    private readonly ISlackProbe _slackProbe;
    private readonly ChatNavigationState? _navigationState;
    private readonly IBrowserAutomationBootstrapper _browserBootstrapper;
    private readonly DeviceFlowServiceFactory? _oauthFactory;

    /// <summary>
    /// The provider descriptor registry. Exposed for use by the page.
    /// </summary>
    public ProviderDescriptorRegistry Registry => _registry;
    private readonly DaemonManager? _daemonManager;
    private readonly DaemonApi? _daemonApi;
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
    public ReactiveProperty<int> SpinnerTick { get; } = new(0);

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

    // ── Step 1 (continued): OAuth flow ──
    public OAuthFlowCoordinator OAuth { get; private set; } = null!; // initialized in constructor

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
    public string SelectedBrowserAutomationBackend { get; set; } = BrowserAutomationMcpProfiles.PlaywrightBackend;
    public bool IsChromeDevToolsAvailable { get; }
    public string ChromeDevToolsUnavailableReason { get; }

    // ── Step 7: Exposure + Notifications ──
    public string? ExposureMode { get; set; }
    public string? WebhookUrl { get; set; }

    // ── Step 8: Identity ──
    public string AgentName { get; set; } = "Netclaw";
    public string? CommunicationStyle { get; set; }
    public string? UserName { get; set; }
    public string UserTimezone { get; set; } = TimeZoneInfo.Local.Id;

    // ── Step 9: Health Check ──
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
        ChatNavigationState? navigationState = null,
        DeviceFlowServiceFactory? oauthFactory = null,
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null)
        : this(paths, registry, registry, slackProbe, null, navigationState, oauthFactory, daemonManager, daemonApi)
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
        ChatNavigationState? navigationState = null,
        DeviceFlowServiceFactory? oauthFactory = null,
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null)
    {
        _paths = paths;
        _probe = probe;
        _registry = registry;
        _slackProbe = slackProbe;
        _navigationState = navigationState;
        _browserBootstrapper = browserBootstrapper ?? new BrowserAutomationBootstrapper();
        _oauthFactory = oauthFactory;
        _daemonManager = daemonManager;
        _daemonApi = daemonApi;

        OAuth = new OAuthFlowCoordinator(
            registry,
            oauthFactory,
            daemonApi,
            RequestRedraw);

        var chromeDetection = BrowserAutomationRuntimeDetector.DetectChrome();
        IsChromeDevToolsAvailable = chromeDetection.IsInstalled;
        ChromeDevToolsUnavailableReason =
            chromeDetection.Reason ?? "local Chrome executable not found";
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
    public int ActiveStepCount => TotalSteps - 1 - (AnyChatServicesEnabled() ? 0 : 1);

    /// <summary>
    /// Returns the display number for the given step, accounting for skipped steps.
    /// </summary>
    public int GetDisplayStepNumber(WizardStep step)
    {
        var num = (int)step;
        if (!AnyChatServicesEnabled() && step > WizardStep.ChatServices)
            num--;
        if (step > WizardStep.Memory)
            num--;
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

        // Skip Memory — SQLite is the only backend, no user input needed
        if (next == WizardStep.Memory)
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

        // Skip Memory going back too
        if (previous == WizardStep.Memory)
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
        var providerType = SelectedProviderType ?? "unknown";
        var probeId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        Exception? probeException = null;
        var credential = ApiKeyInput;
        if (string.IsNullOrWhiteSpace(credential)
            && SelectedAuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce
            && OAuth.Result is not null)
        {
            credential = OAuth.Result.AccessToken.Value;
        }

        IsProbing.Value = true;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        RequestRedraw();

        ProbeDiagnosticsLog.Write(
            _paths,
            "init-wizard",
            providerType,
            EndpointInput,
            probeId,
            "start",
            $"auth={SelectedAuthMethod} credentialPresent={!string.IsNullOrWhiteSpace(credential)}");

        // Fire-and-forget timer — self-cancels via the shared CTS.
        // RunProbeTimerAsync handles OperationCanceledException internally
        // and exits cleanly, so no need to await it after cancellation.
        _ = RunProbeTimerAsync(ct);

        var result = new ProviderProbeResult(false, "Validation failed before probe completed.", []);
        try
        {
            result = await _probe.ProbeAsync(
                    providerType,
                    EndpointInput,
                    credential,
                    SelectedAuthMethod,
                    ct)
                .WaitAsync(ProbeHardTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ProviderProbeResult(false, "Validation cancelled.", []);
        }
        catch (TimeoutException)
        {
            result = new ProviderProbeResult(false,
                $"Validation timed out after {(int)ProbeHardTimeout.TotalSeconds} seconds. Check network connectivity and try again.", []);
        }
        catch (Exception ex)
        {
            probeException = ex;
            result = new ProviderProbeResult(false, $"Validation failed: {ex.Message}", []);
        }
        finally
        {
            // Stop the timer and cancel any in-flight probe request
            CancelProbe();

            ProbeDiagnosticsLog.Write(
                _paths,
                "init-wizard",
                providerType,
                EndpointInput,
                probeId,
                result.Success ? "success" : "failure",
                result.ErrorMessage,
                stopwatch.Elapsed,
                probeException);
        }

        DiscoveredModels.Clear();
        if (result.Success)
            DiscoveredModels.AddRange(result.Models);

        // Clear probing state before publishing final result so subscribers that
        // render based on both values don't get stuck on the spinner frame.
        IsProbing.Value = false;
        ProbeResult.Value = result;
        RequestRedraw();
    }

    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        var tickCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(120, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            tickCount++;
            SpinnerTick.Value = tickCount;

            // Update elapsed seconds every ~1 second (every 8 ticks at 120ms)
            if (tickCount % 8 == 0)
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

    /// <summary>
    /// Start the OAuth device flow. Called by the page when the user selects OAuth device flow.
    /// </summary>
    public void StartOAuthFlow()
    {
        if (SelectedProviderType is null) return;
        ProbeElapsedSeconds.Value = 0;
        var ct = OAuth.StartDeviceFlow(SelectedProviderType, result =>
        {
            ApiKeyInput = result.AccessToken.Value;
            StartProbe();
        });
        _ = RunProbeTimerAsync(ct);
    }

    /// <summary>
    /// Start the browser-based OAuth flow. Called by the page when the user selects OAuth PKCE.
    /// </summary>
    public void StartBrowserOAuthFlow()
    {
        if (SelectedProviderType is null) return;
        ProbeElapsedSeconds.Value = 0;
        var ct = OAuth.StartBrowserFlow(SelectedProviderType, result =>
        {
            ApiKeyInput = result.AccessToken.Value;
            StartProbe();
        });
        _ = RunProbeTimerAsync(ct);
    }

    /// <summary>
    /// Handle a pasted redirect URL for browser OAuth fallback.
    /// </summary>
    public Task SubmitRedirectUrlAsync(string? pastedUrl)
        => OAuth.SubmitRedirectUrlAsync(pastedUrl);

    internal void ClearFromProvider()
    {
        CancelProbe();
        OAuth.Reset();
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
        using var overallCts = new CancellationTokenSource(OverallHealthCheckTimeout);
        try
        {
            await RunHealthCheckCoreAsync(overallCts.Token);
        }
        catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
        {
            HealthCheckResults.Add(new HealthCheckItem("Health check timed out", false));
            IsHealthCheckRunning.Value = false;
            IsComplete.Value = true;
            NotifyHealthCheckChanged();
            StatusMessage.Value = "Setup timed out. Run `netclaw daemon start` to begin.";
        }
    }

    private async Task RunHealthCheckCoreAsync(CancellationToken ct)
    {
        IsHealthCheckRunning.Value = true;
        IsComplete.Value = false;
        HealthCheckResults.Clear();
        NotifyHealthCheckChanged();

        // Provider check
        HealthCheckResults.Add(new HealthCheckItem("LLM provider configured", null));
        NotifyHealthCheckChanged();
        await Task.Delay(200, ct); // simulate validation

        var providerOk = !string.IsNullOrWhiteSpace(SelectedProviderType);
        HealthCheckResults[^1] = new HealthCheckItem(
            $"LLM provider configured ({SelectedProviderType ?? "none"})",
            providerOk);
        NotifyHealthCheckChanged();

        // Model check
        HealthCheckResults.Add(new HealthCheckItem("Model selected", null));
        NotifyHealthCheckChanged();
        await Task.Delay(200, ct);

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
            try
            {
                using var slackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                slackCts.CancelAfter(SlackProbeHardTimeout);
                var slackResult = await _slackProbe.ProbeAsync(SlackBotToken!, slackCts.Token);
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    "Slack auth timed out (15s). Check your network connection.", false);
            }
        }
        NotifyHealthCheckChanged();

        // Channel resolution — only when auth succeeded and names were provided
        var parsedChannelNames = ParseChannelNames(SlackChannelNamesInput);
        if (slackAuthOk && parsedChannelNames.Count > 0)
        {
            HealthCheckResults.Add(new HealthCheckItem("Resolving Slack channels", null));
            NotifyHealthCheckChanged();

            try
            {
                using var channelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                channelCts.CancelAfter(ChannelResolutionHardTimeout);
                LastChannelResolution = await _slackProbe.ResolveChannelNamesAsync(
                    SlackBotToken!, parsedChannelNames, channelCts.Token);

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
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    "Slack channel resolution timed out (35s). Check your network connection.", false);
            }
            NotifyHealthCheckChanged();
        }

        // Memory backend check — SQLite is the only backend
        HealthCheckResults.Add(new HealthCheckItem("Memory backend (SQLite)", true));
        NotifyHealthCheckChanged();

        // Browser automation prerequisites (optional)
        if (BrowserAutomationEnabled)
        {
            HealthCheckResults.Add(new HealthCheckItem("Browser automation prerequisites", null));
            NotifyHealthCheckChanged();

            try
            {
                using var browserCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                browserCts.CancelAfter(BrowserBootstrapHardTimeout);
                var bootstrap = await _browserBootstrapper.EnsureReadyAsync(
                    SelectedBrowserAutomationBackend, browserCts.Token);
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                HealthCheckResults[^1] = new HealthCheckItem(
                    "Browser automation setup timed out (3m). Try again or skip browser automation.", false);
                NotifyHealthCheckChanged();
            }
        }

        // Stop daemon before writing config to prevent ConfigWatcherService restart race
        if (_daemonManager is not null)
        {
            var status = _daemonManager.GetStatus();
            if (status.IsRunning)
            {
                HealthCheckResults.Add(new HealthCheckItem("Stopping daemon for config update", null));
                NotifyHealthCheckChanged();

                var stopResult = await _daemonManager.StopAsync();
                HealthCheckResults[^1] = stopResult.Success
                    ? new HealthCheckItem("Daemon stopped", true)
                    : new HealthCheckItem($"Daemon stop failed: {stopResult.Message}", false);
                NotifyHealthCheckChanged();
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

            var daemonOk = await StartAndPollDaemonAsync(ct);
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
            if (_navigationState is not null)
                _navigationState.InitialMessage = BuildOnboardingTrigger();
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
    private async Task<bool> StartAndPollDaemonAsync(CancellationToken ct = default)
    {
        if (_daemonManager is null)
            return false;

        var result = _daemonManager.Start();
        if (!result.Success && !result.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_daemonApi is not null && await _daemonApi.IsHealthyAsync(ct))
                    return true;
            }
            catch (HttpRequestException)
            {
                HealthCheckResults[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyHealthCheckChanged();
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                HealthCheckResults[^1] = new HealthCheckItem($"Starting daemon ({i + 1}s)", null);
                NotifyHealthCheckChanged();
            }

            await Task.Delay(1000, ct);
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
                     && desc.Auth is EndpointOnlyAuth)
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

            var userIds = ParseUserIds(SlackAllowedUserIdsInput);
            if (SlackAllowDirectMessages)
            {
                slackSection["AllowDirectMessages"] = true;
            }

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

        // Skill sync section
        config["SkillSync"] = new Dictionary<string, object>
        {
            ["DisableSystemSkillSync"] = false
        };

        // MCP servers: browser automation (optional)
        var mcpServers = new Dictionary<string, object>();

        if (BrowserAutomationEnabled)
        {
            var (profileName, entry) = BrowserAutomationMcpProfiles.Create(SelectedBrowserAutomationBackend);
            mcpServers[profileName] = new Dictionary<string, object?>
            {
                ["Transport"] = entry.Transport,
                ["Command"] = entry.Command,
                ["Arguments"] = entry.Arguments,
                ["EnvironmentVariables"] = entry.EnvironmentVariables,
                ["Enabled"] = entry.Enabled,
                ["GrantCategory"] = entry.GrantCategory
            };
        }

        if (mcpServers.Count > 0)
            config["McpServers"] = mcpServers;

        // Notifications section (optional webhook)
        if (!string.IsNullOrWhiteSpace(WebhookUrl))
        {
            config["Notifications"] = new Dictionary<string, object>
            {
                ["Webhooks"] = new object[]
                {
                    new Dictionary<string, object> { ["Url"] = WebhookUrl }
                }
            };
        }

        // Write identity files
        WriteIdentityFiles();

        // Seed default subagent definitions
        SeedBuiltInAgents();

        // Write netclaw.json
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, jsonOptions));

        // Provider credentials — use shared writer (handles OAuthTokenExpiry placement)
        if (!string.IsNullOrWhiteSpace(SelectedProviderType))
        {
            ProviderCredentialWriter.WriteProvider(
                _paths,
                SelectedProviderType.ToLowerInvariant(),
                SelectedProviderType.ToLowerInvariant(),
                SelectedAuthMethod,
                EndpointInput,
                OAuth.Result,
                ApiKeyInput,
                _registry,
                SensitiveStringTypeConverter.Protector);
        }

        // Non-provider secrets (Slack, Search)
        var secrets = new Dictionary<string, object>();

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
            SecretsFileWriter.Write(_paths.SecretsPath, secrets,
                options: jsonOptions, protector: SensitiveStringTypeConverter.Protector);
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

        File.WriteAllText(_paths.SoulPath,
            $"""
            # You are {name}

            ## Communication Style
            {styleDescription}

            ## User
            - Name: {userName}
            - Timezone: {timezone}
            """);

        File.WriteAllText(_paths.AgentsPath,
            $"""
            # Operating Rules

            - Act autonomously — use available tools to accomplish tasks
            - For MCP capabilities, use progressive discovery: search_tools("servers") -> search_tools("<intent>", server: "<server_name>")
            - For interactive web tasks (clicking, typing, form filling), use browser MCP tools
            - For browser automation, prefer file outputs over inline page dumps

            ## Autonomy Rules

            - If the user asks you to do something, DO IT in the same response. Do not split
              intent ("I'll do that") from action (tool calls) across turns.
            - NEVER say "On it" or "Roger that" without making tool calls in the same response.
            - Read-only tool use (search, fetch, read, list) requires NO permission. Just do it.
            - Only ask before destructive actions (file deletion, infrastructure changes).
            - Maximum one clarification question per task. After that, proceed with best judgment.
            - When one approach fails, try alternatives immediately. Do not report failure
              without attempting at least one fallback.
            - Never say "you can visit..." or "you can call..." — look it up yourself.

            ## Grounding Rules

            - Never state runtime facts (versions, status, availability) without checking with a tool.
            - Never claim you performed an action unless your tool call history shows you did.
            - Never claim a tool doesn't exist without calling search_tools first.
            - Never silently substitute a different answer. If you can't complete the actual task,
              say so explicitly. Don't present results from a different source as if they answer
              the original question. Tell the user what failed and ask how to proceed.
            - "I don't know" beats a confident wrong answer.

            ## Search Decision Rules

            Use web_search IMMEDIATELY (do not ask first) when the user's question involves:
            - Prices, availability, stock, deals, or comparisons
            - Current events, news, or anything that changes over time
            - Specific products, services, businesses, or competitors
            - Travel: flights, hotels, bookings, availability
            - Local info: restaurants, stores, services near a location
            - Any verifiable factual claim you are not certain of

            Do NOT search for: stable concepts, definitions, how-things-work, math, coding, opinions.

            When in doubt, search. A redundant search costs seconds; a hallucinated fact costs trust.

            After searching: every specific claim MUST include an inline hyperlink to its source.
            Format: [descriptive text](url) — no footnotes, no [1]-style references.
            No URL means do not state the fact.

            **Full citation & search guidance:** `file_read("{_paths.SystemSkillsDirectory}/search-citation/SKILL.md")`

            ## Media Attachments

            When a user sends an image or file, it is saved to the session media directory.
            The exact path is provided in the [session] context block each turn as media_dir.
            Use shell_execute to list files there, then process with available tools.
            Do not claim you cannot access user-attached media.

            ## Scheduling

            When the user says "remind me", "every day at", "check this weekly", "schedule",
            or any time-based instruction: use set_reminder immediately. Do not explain how
            reminders work — create the reminder.

            **Full scheduling parameters, CLI commands, and Netclaw operations:**
            `file_read("{_paths.SystemSkillsDirectory}/netclaw-manual/SKILL.md")`

            ## Subagent Delegation

            Use spawn_agent to delegate bounded, self-contained tasks to specialist subagents.
            Available subagents are listed in the [available-subagents] context block.

            When to delegate:
            - Deep web research that requires multiple searches and synthesis
            - Code analysis tasks on large files or multiple files
            - Summarization of long documents or web pages

            When NOT to delegate:
            - Simple searches (use web_search directly)
            - Tasks requiring MCP tools (subagents only have web_search, web_fetch,
              file_read, attach_file)
            - Interactive browser tasks (subagents cannot use browser MCP tools)

            spawn_agent is NOT the same as search_tools. Subagents are named specialists
            (e.g., "research-assistant", "code-analyst", "summarizer"). MCP tools are
            discovered via search_tools.

            ## Skill Reference

            For detailed guidance beyond these summary rules, load skills with file_read:

            | Load when... | Skill |
            |-------------|-------|
            | Doing web searches, need citation format, verifying facts | `{_paths.SystemSkillsDirectory}/search-citation/SKILL.md` |
            | Need tool catalog, grant categories, scheduling params, MCP discovery, subagent delegation, CLI commands, health endpoints | `{_paths.SystemSkillsDirectory}/netclaw-manual/SKILL.md` |
            | User asks what you remember, wants to save/recall/correct cross-session knowledge, or you need more than automatic recall | `{_paths.SystemSkillsDirectory}/netclaw-memory/SKILL.md` |
            | User wants to update lasting preferences, profile, tone, workflow rules, or environment capabilities | `{_paths.SystemSkillsDirectory}/netclaw-identity/SKILL.md` |
            | Session/tool failure, missing capabilities, daemon health issues, debugging what happened | `{_paths.SystemSkillsDirectory}/netclaw-diagnostics/SKILL.md` |
            | A repeatable workflow emerges and should become a skill file | `{_paths.SystemSkillsDirectory}/skill-authoring/SKILL.md` |

            ## Identity Files

            Identity configuration lives in `{_paths.IdentityDirectory}/`:

            | File | Purpose |
            |------|---------|
            | `{_paths.SoulPath}` | Personality, tone, user profile |
            | `{_paths.AgentsPath}` | Operating rules, meta-guidance (this file) |
            | `{_paths.ToolingPath}` | Host environment capabilities |

            To update these files, use `file_read` to check current content first, then `file_write` to update.
            Keep top-level files concise. For depth, create detail files in matching subdirectories:
            `{_paths.SoulDetailDirectory}/`, `{_paths.AgentsDetailDirectory}/`, `{_paths.ToolingDetailDirectory}/`

            ## Memory Triage

            | Information Type | Destination |
            |-----------------|-------------|
            | Personal facts (name, family, preferences) | `SOUL.md` |
            | Operating rules, workflow preferences | `AGENTS.md` |
            | Environment capabilities, tool configs | `TOOLING.md` |
            | World knowledge, project details, solutions | Memory tools (`store_memory`, `find_memories`) |
            | Procedures, reusable workflows | Skill files in `{_paths.SkillsDirectory}/` |

            ## Cross-Session Memory

            Use `find_memories` to recall information from prior sessions, saved knowledge,
            or project context. Save important findings proactively with `store_memory`.
            """);

        File.WriteAllText(_paths.ToolingPath,
            """
            # Environment Capabilities

            No capabilities discovered yet. Run `netclaw doctor` or ask Netclaw to probe your environment.

            # Source Code
            - **Repository:** https://github.com/Aaronontheweb/netclaw (private)
            """);
    }

    internal string BuildOnboardingTrigger()
    {
        var userName = string.IsNullOrWhiteSpace(UserName) ? "User" : UserName;
        var commStyle = CommunicationStyle ?? "Concise & casual";
        var soulPath = _paths.SoulPath;

        return $"""
            I just finished setting up. My name is {userName} and I chose "{commStyle}" as my communication style.

            This is our first conversation. I'd like you to get to know me so you can be more helpful. Please:

            1. Introduce yourself briefly
            2. Ask me what I'd primarily like to use you for
            3. Ask if there's anything else you should know about me — my background, how I work, tools I use, preferences, etc.
            4. After our conversation, update my profile in SOUL.md ({soulPath}) with what you've learned. Use file_read to check current content first, then file_write to update it. Keep the existing structure but enrich it with the details from our conversation.

            Keep it natural and conversational — don't ask everything at once.
            """;
    }

    /// <summary>
    /// Seeds default subagent definition files to the agents directory.
    /// Does not overwrite existing files so operator customizations are preserved.
    /// </summary>
    internal void SeedBuiltInAgents()
    {
        var agentsDir = _paths.AgentsDirectory;
        Directory.CreateDirectory(agentsDir);

        SeedAgentFile(agentsDir, "research-assistant.json", """
            {
              "name": "research-assistant",
              "description": "Deep web research with search and citation",
              "systemPromptFile": "research-assistant.md",
              "tools": ["web_search", "web_fetch", "file_read", "attach_file"],
              "modelRole": "Compaction",
              "timeoutSeconds": 120
            }
            """);

        SeedAgentFile(agentsDir, "research-assistant.md", """
            You are a research assistant. Your job is to help the user by searching the
            web, gathering information from multiple sources, and synthesizing findings
            into clear, well-organized summaries.

            ## Guidelines

            - Search for information using web_search, then fetch relevant pages with web_fetch.
            - Cross-reference multiple sources when possible.
            - Always cite your sources with URLs.
            - Use file_read to inspect local reference material when needed.
            - Use attach_file when the parent session needs to deliver an existing file.
            - Be thorough but concise — focus on facts and actionable information.
            - Use markdown formatting for structure (headers, lists, code blocks).
            - If a search returns no useful results, say so rather than guessing.
            """);

        SeedAgentFile(agentsDir, "code-analyst.json", """
            {
              "name": "code-analyst",
              "description": "Analyze code, run commands, and review files",
              "systemPromptFile": "code-analyst.md",
              "tools": ["file_read"],
              "modelRole": "Compaction",
              "timeoutSeconds": 120
            }
            """);

        SeedAgentFile(agentsDir, "code-analyst.md", """
            You are a code analyst. Your job is to read source code, run build and test
            commands, and provide clear analysis of code quality, structure, and issues.

            ## Guidelines

            - Read files with file_read to understand code structure.
            - Report findings with file paths and line numbers.
            - Focus on actionable observations — bugs, performance issues, design concerns.
            - Use markdown formatting with code blocks for examples.
            - Do not modify code or run commands directly; return analysis for the parent session to act on.
            """);

        SeedAgentFile(agentsDir, "summarizer.json", """
            {
              "name": "summarizer",
              "description": "Summarize documents and content concisely",
              "systemPromptFile": "summarizer.md",
              "tools": ["file_read"],
              "modelRole": "Compaction",
              "timeoutSeconds": 60
            }
            """);

        SeedAgentFile(agentsDir, "summarizer.md", """
            You are a summarizer. Your job is to read content and produce concise,
            structured summaries that capture the essential information.

            ## Guidelines

            - Focus on key facts, decisions, and action items.
            - Use bullet points and headers for scannable structure.
            - Preserve important details like names, dates, numbers, and links.
            - Omit filler, repetition, and low-signal content.
            - Keep summaries under 500 words unless the source material is very long.
            - If summarizing code, highlight the main purpose, public API, and key patterns.
            """);
    }

    private static void SeedAgentFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            return; // Do not overwrite operator customizations

        File.WriteAllText(path, content);
    }

    public override void Dispose()
    {
        CancelProbe();
        OAuth.Dispose();
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
