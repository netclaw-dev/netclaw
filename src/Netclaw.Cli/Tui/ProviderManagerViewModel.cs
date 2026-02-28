using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// States for the provider manager TUI.
/// </summary>
public enum ProviderManagerState
{
    List,
    AddSelectType,
    AddSelectAuth,
    AddCredentials,
    AddValidating,
    AddComplete,
    RemoveConfirm
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw provider</c> interactive TUI.
/// Manages browsing, adding, and removing provider configurations.
/// </summary>
public sealed class ProviderManagerViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;
    private readonly IProviderProbe _probe;
    private CancellationTokenSource? _probeCts;

    public ReactiveProperty<ProviderManagerState> CurrentState { get; } = new(ProviderManagerState.List);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);

    /// <summary>
    /// Version counter for state changes that require DynamicLayoutNode invalidation.
    /// </summary>
    internal ReactiveProperty<int> StateVersion { get; } = new(0);

    // ── Loaded providers ──
    public List<(string Name, ProviderEntry Entry)> Providers { get; } = [];
    public int SelectedProviderIndex { get; set; }

    // ── Add flow state ──
    public string? NewProviderName { get; set; }
    public string? NewProviderType { get; set; }
    public AuthMethod NewAuthMethod { get; set; } = AuthMethod.None;
    public string? NewApiKey { get; set; }
    public string? NewEndpoint { get; set; }

    // ── Remove flow state ──
    public string? RemoveProviderName { get; set; }
    public List<string> RemoveBlockingRoles { get; } = [];

    /// <summary>
    /// Completes when the provider probe finishes. Used for testing.
    /// </summary>
    internal Task? ProbeCompletion { get; private set; }

    public ProviderManagerViewModel(NetclawPaths paths, IProviderProbe probe)
    {
        _paths = paths;
        _probe = probe;
    }

    public override void OnActivated()
    {
        base.OnActivated();
        RefreshProviders();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleGlobalKey)
            .DisposeWith(Subscriptions);
    }

    public void RefreshProviders()
    {
        Providers.Clear();
        var loaded = Provider.ProviderCommand.LoadProviders(_paths);
        foreach (var (name, entry) in loaded.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            Providers.Add((name, entry));
    }

    /// <summary>
    /// Enter the add-provider flow.
    /// </summary>
    public void StartAdd()
    {
        ClearAddState();
        CurrentState.Value = ProviderManagerState.AddSelectType;
        NotifyStateChanged();
    }

    /// <summary>
    /// Select provider type and advance to auth selection (or credentials for auth-free providers).
    /// </summary>
    public void SelectProviderType(string type)
    {
        NewProviderType = type;
        // Auto-generate a default name
        NewProviderName = GenerateProviderName(type);

        var supportedAuth = ProviderCapabilities.GetSupportedAuthMethods(type);
        if (supportedAuth is [AuthMethod.None])
        {
            NewAuthMethod = AuthMethod.None;
            CurrentState.Value = ProviderManagerState.AddCredentials;
        }
        else
        {
            CurrentState.Value = ProviderManagerState.AddSelectAuth;
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Select auth method and advance to credential input.
    /// </summary>
    public void SelectAuthMethod(AuthMethod method)
    {
        NewAuthMethod = method;
        CurrentState.Value = ProviderManagerState.AddCredentials;
        NotifyStateChanged();
    }

    /// <summary>
    /// Submit credentials and start validation probe.
    /// </summary>
    public void SubmitCredentials()
    {
        if (NewAuthMethod == AuthMethod.ApiKey && string.IsNullOrWhiteSpace(NewApiKey))
        {
            StatusMessage.Value = "API key is required.";
            RequestRedraw();
            return;
        }

        CurrentState.Value = ProviderManagerState.AddValidating;
        NotifyStateChanged();
        StartProbe();
    }

    /// <summary>
    /// Write the new provider to config files after successful validation.
    /// </summary>
    public void ConfirmAdd()
    {
        WriteProviderConfig();
        RefreshProviders();
        CurrentState.Value = ProviderManagerState.List;
        StatusMessage.Value = $"Added provider '{NewProviderName}'. Restart daemon for changes to take effect.";
        ClearAddState();
        NotifyStateChanged();
    }

    /// <summary>
    /// Start remove confirmation for the currently selected provider.
    /// </summary>
    public void StartRemove()
    {
        if (Providers.Count == 0 || SelectedProviderIndex >= Providers.Count)
            return;

        var (name, _) = Providers[SelectedProviderIndex];
        RemoveProviderName = name;
        RemoveBlockingRoles.Clear();

        var roles = Provider.ProviderCommand.GetReferencingModelRoles(name, _paths);
        RemoveBlockingRoles.AddRange(roles);

        CurrentState.Value = ProviderManagerState.RemoveConfirm;
        NotifyStateChanged();
    }

    /// <summary>
    /// Execute provider removal after confirmation.
    /// </summary>
    public void ConfirmRemove()
    {
        if (RemoveBlockingRoles.Count > 0 || RemoveProviderName is null)
        {
            GoBackToList();
            return;
        }

        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(_paths);

        var providers = ConfigFileHelper.GetSectionOrNull(config, "Providers");
        if (providers?.Remove(RemoveProviderName) == true)
            ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);

        var secretProviders = ConfigFileHelper.GetSectionOrNull(secrets, "Providers");
        if (secretProviders?.Remove(RemoveProviderName) == true)
            ConfigFileHelper.WriteConfigFile(_paths.SecretsPath, secrets);

        RefreshProviders();
        StatusMessage.Value = $"Removed provider '{RemoveProviderName}'. Restart daemon for changes to take effect.";
        RemoveProviderName = null;
        CurrentState.Value = ProviderManagerState.List;
        NotifyStateChanged();
    }

    public void GoBackToList()
    {
        CancelProbe();
        ClearAddState();
        RemoveProviderName = null;
        RemoveBlockingRoles.Clear();
        StatusMessage.Value = "";
        CurrentState.Value = ProviderManagerState.List;
        NotifyStateChanged();
    }

    public void GoBack()
    {
        switch (CurrentState.Value)
        {
            case ProviderManagerState.AddSelectType:
                GoBackToList();
                break;
            case ProviderManagerState.AddSelectAuth:
                CurrentState.Value = ProviderManagerState.AddSelectType;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddCredentials:
                var supportedAuth = ProviderCapabilities.GetSupportedAuthMethods(NewProviderType ?? "");
                if (supportedAuth is [AuthMethod.None])
                    CurrentState.Value = ProviderManagerState.AddSelectType;
                else
                    CurrentState.Value = ProviderManagerState.AddSelectAuth;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddValidating:
                CancelProbe();
                CurrentState.Value = ProviderManagerState.AddCredentials;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddComplete:
                GoBackToList();
                break;
            case ProviderManagerState.RemoveConfirm:
                GoBackToList();
                break;
            default:
                Shutdown();
                break;
        }
    }

    public void RequestQuit()
    {
        Shutdown();
    }

    // ── Probe ──

    internal void StartProbe()
    {
        CancelProbe();
        ProbeCompletion = ProbeProviderAsync();
    }

    internal void CancelProbe()
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

        _ = RunProbeTimerAsync(ct);

        var result = await _probe.ProbeAsync(
            NewProviderType ?? "unknown",
            NewEndpoint,
            NewApiKey,
            ct);

        CancelProbe();
        ProbeResult.Value = result;
        IsProbing.Value = false;

        if (result.Success)
        {
            CurrentState.Value = ProviderManagerState.AddComplete;
        }

        NotifyStateChanged();
    }

    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }

            ProbeElapsedSeconds.Value++;
            RequestRedraw();
        }
    }

    // ── Config writing ──

    private void WriteProviderConfig()
    {
        _paths.EnsureDirectoriesExist();

        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(_paths);

        var providers = ConfigFileHelper.GetOrCreateSection(config, "Providers");
        var providerEntry = new Dictionary<string, object>
        {
            ["Type"] = NewProviderType!
        };

        if (NewAuthMethod != AuthMethod.None)
            providerEntry["AuthMethod"] = NewAuthMethod.ToString();

        var endpoint = NewEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint) && NewProviderType == "ollama")
            endpoint = ProviderCapabilities.GetDefaultEndpoint("ollama");
        endpoint ??= ProviderCapabilities.GetDefaultEndpoint(NewProviderType!);

        providerEntry["Endpoint"] = endpoint;
        providers[NewProviderName!] = providerEntry;
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);

        if (!string.IsNullOrWhiteSpace(NewApiKey))
        {
            var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
            secretProviders[NewProviderName!] = new Dictionary<string, object>
            {
                ["ApiKey"] = NewApiKey
            };
            ConfigFileHelper.WriteConfigFile(_paths.SecretsPath, secrets);
        }
    }

    // ── Helpers ──

    private string GenerateProviderName(string type)
    {
        var baseName = $"my-{type}";
        if (!Providers.Any(p => string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"my-{type}-{i}";
            if (!Providers.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        return $"my-{type}-{Guid.NewGuid():N[..6]}";
    }

    private void ClearAddState()
    {
        CancelProbe();
        NewProviderName = null;
        NewProviderType = null;
        NewAuthMethod = AuthMethod.None;
        NewApiKey = null;
        NewEndpoint = null;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
    }

    private void NotifyStateChanged()
    {
        StateVersion.Value++;
        RequestRedraw();
    }

    private void HandleGlobalKey(KeyPressed key)
    {
        if (key.KeyInfo.Key == ConsoleKey.Q &&
            key.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Shutdown();
        }
    }

    public override void Dispose()
    {
        CancelProbe();
        CurrentState.Dispose();
        StatusMessage.Dispose();
        IsProbing.Dispose();
        ProbeResult.Dispose();
        ProbeElapsedSeconds.Dispose();
        StateVersion.Dispose();
        base.Dispose();
    }
}
