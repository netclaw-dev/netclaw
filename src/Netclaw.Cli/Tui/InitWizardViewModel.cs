using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using Netclaw.Configuration;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Wizard steps for the init flow.
/// </summary>
public enum WizardStep
{
    Provider = 1,
    Slack = 2,
    Acl = 3,
    Mcp = 4,
    Exposure = 5,
    HealthCheck = 6
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw init</c> onboarding wizard.
/// Drives a 6-step wizard state machine with back-navigation support.
/// </summary>
public partial class InitWizardViewModel : ReactiveViewModel
{
    public const int TotalSteps = 6;

    private readonly NetclawPaths _paths;

#pragma warning disable CS0169, CS0414
    [Reactive] private WizardStep _currentStep = WizardStep.Provider;
    [Reactive] private string _statusMessage = "";
    [Reactive] private bool _isHealthCheckRunning;
    [Reactive] private bool _isComplete;
#pragma warning restore CS0169, CS0414

    // ── Step 1: Provider ──
    public string? SelectedProviderType { get; set; }
    public AuthMethod SelectedAuthMethod { get; set; } = AuthMethod.None;
    public string? ApiKeyInput { get; set; }
    public string? EndpointInput { get; set; }

    // ── Step 2: Slack ──
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

    public InitWizardViewModel(NetclawPaths paths)
    {
        _paths = paths;
    }

    public override void OnActivated()
    {
        base.OnActivated();

        Input.OfType<KeyPressed>()
            .Subscribe(HandleGlobalKey)
            .DisposeWith(Subscriptions);
    }

    /// <summary>
    /// Advance to the next wizard step, or write config on the final step.
    /// </summary>
    public void GoNext()
    {
        if (CurrentStep == WizardStep.HealthCheck)
        {
            if (!IsHealthCheckRunning && !IsComplete)
                _ = RunHealthCheckAsync();
            return;
        }

        var next = (WizardStep)((int)CurrentStep + 1);
        CurrentStep = next;
        StatusMessage = "";
        RequestRedraw();
    }

    /// <summary>
    /// Go back one step. Clears downstream state per the design doc's
    /// back-navigation clearing rules.
    /// </summary>
    public void GoBack()
    {
        if (CurrentStep == WizardStep.Provider)
        {
            // Can't go back from step 1 — quit
            Shutdown();
            return;
        }

        var previous = (WizardStep)((int)CurrentStep - 1);

        // Back-navigation clearing rules from design doc:
        // Provider change clears auth + model downstream
        if (previous == WizardStep.Provider)
        {
            ClearFromProvider();
        }

        CurrentStep = previous;
        StatusMessage = "";
        RequestRedraw();
    }

    public void RequestQuit()
    {
        Shutdown();
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
        SelectedAuthMethod = AuthMethod.None;
        ApiKeyInput = null;
        EndpointInput = null;
    }

    private async Task RunHealthCheckAsync()
    {
        IsHealthCheckRunning = true;
        HealthCheckResults.Clear();
        RequestRedraw();

        // Provider check
        HealthCheckResults.Add(new HealthCheckItem("LLM provider configured", null));
        RequestRedraw();
        await Task.Delay(200); // simulate validation

        var providerOk = !string.IsNullOrWhiteSpace(SelectedProviderType);
        HealthCheckResults[^1] = new HealthCheckItem(
            $"LLM provider configured ({SelectedProviderType ?? "none"})",
            providerOk);
        RequestRedraw();

        // Slack check
        HealthCheckResults.Add(new HealthCheckItem("Slack configuration", null));
        RequestRedraw();
        await Task.Delay(200);

        var slackOk = !SlackEnabled ||
                       (!string.IsNullOrWhiteSpace(SlackBotToken) &&
                        !string.IsNullOrWhiteSpace(SlackAppToken));
        HealthCheckResults[^1] = new HealthCheckItem(
            SlackEnabled
                ? "Slack configuration (Socket Mode)"
                : "Slack configuration (disabled)",
            slackOk);
        RequestRedraw();

        // Config write
        HealthCheckResults.Add(new HealthCheckItem("Writing configuration", null));
        RequestRedraw();

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

        IsHealthCheckRunning = false;
        IsComplete = true;
        StatusMessage = "Setup complete! Run `netclaw daemon start` to begin.";
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

        // Models section — set the main model provider
        if (!string.IsNullOrWhiteSpace(SelectedProviderType))
        {
            config["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = SelectedProviderType.ToLowerInvariant()
                }
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
        DisposeReactiveFields();
        base.Dispose();
    }
}

/// <summary>
/// Result of a single health check probe.
/// </summary>
/// <param name="Label">Display text.</param>
/// <param name="Passed">Null while running, true/false when complete.</param>
public sealed record HealthCheckItem(string Label, bool? Passed);
