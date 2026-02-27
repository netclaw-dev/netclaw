using System.Text.Json;
using Netclaw.Configuration;
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
    Mcp = 4,
    Exposure = 5,
    HealthCheck = 6
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw init</c> onboarding wizard.
/// Drives a 6-step wizard state machine with back-navigation support.
/// ACL step is conditionally skipped when no chat services are enabled.
/// </summary>
public partial class InitWizardViewModel : ReactiveViewModel
{
    public const int TotalSteps = 6;

    private readonly NetclawPaths _paths;
    private readonly IProviderProbe _probe;
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

    // ── Step 3: ACL ──
    public string? OwnerIdentity { get; set; }

    // ── Step 4: MCP ──
    public string? McpSelection { get; set; }

    // ── Step 5: Exposure ──
    public string? ExposureMode { get; set; }

    // ── Step 6: Health Check ──
    public List<HealthCheckItem> HealthCheckResults { get; } = [];

    /// <summary>
    /// Completes when the health check finishes. Used for testing without polling.
    /// </summary>
    internal Task? HealthCheckCompletion { get; private set; }

    /// <summary>
    /// Completes when the provider probe finishes. Used for testing without polling.
    /// </summary>
    internal Task? ProbeCompletion { get; private set; }

    public InitWizardViewModel(NetclawPaths paths, IProviderProbe probe)
    {
        _paths = paths;
        _probe = probe;
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

    private async Task RunHealthCheckAsync()
    {
        IsHealthCheckRunning.Value = true;
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
        await Task.Delay(200);

        var slackOk = !SlackEnabled ||
                       (!string.IsNullOrWhiteSpace(SlackBotToken) &&
                        !string.IsNullOrWhiteSpace(SlackAppToken));
        HealthCheckResults[^1] = new HealthCheckItem(
            SlackEnabled
                ? "Slack configuration (Socket Mode)"
                : "Slack configuration (disabled)",
            slackOk);
        NotifyHealthCheckChanged();

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

        IsHealthCheckRunning.Value = false;
        IsComplete.Value = true;
        StatusMessage.Value = "Setup complete! Run `netclaw daemon start` to begin.";
        NotifyHealthCheckChanged();
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
        var config = new Dictionary<string, object>();

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
            else if (providerName == "ollama")
                providerEntry["Endpoint"] = "http://localhost:11434";

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
            config["Slack"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["SocketMode"] = true
            };
        }

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

        if (secrets.Count > 0)
        {
            File.WriteAllText(_paths.SecretsPath,
                JsonSerializer.Serialize(secrets, jsonOptions));
        }
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
