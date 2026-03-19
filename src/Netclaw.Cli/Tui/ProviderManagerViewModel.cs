using System.Diagnostics;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Configuration.Secrets;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// States for the provider manager TUI.
/// </summary>
public enum ProviderManagerState
{
    Loading,
    List,
    AddSelectAuth,
    AddCredentials,
    AddOAuthDeviceFlow,
    AddBrowserOAuthFlow,
    AddValidating,
    AddComplete,
    Details,
    FixCredentials,
    RemoveConfirm
}

/// <summary>
/// Health status of a provider as determined by probing.
/// </summary>
public enum ProviderHealthStatus
{
    Unchecked,
    Probing,
    Healthy,
    Unhealthy
}

/// <summary>
/// Display model for a single row in the provider list.
/// Mutable so probe results can update Health/ProbeResult in place.
/// </summary>
public sealed class ProviderDisplayItem
{
    public string ProviderType { get; init; } = "";
    public bool IsConfigured { get; init; }
    public string? ConfiguredName { get; init; }
    public ProviderEntry? Entry { get; init; }
    public ProviderHealthStatus Health { get; set; } = ProviderHealthStatus.Unchecked;
    public ProviderProbeResult? ProbeResult { get; set; }
    public string DisplayEndpoint { get; init; } = "";
    public string DisplayAuth { get; init; } = "\u2014";
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw provider</c> interactive TUI.
/// Shows all known provider types as a dashboard with health status,
/// and provides context-sensitive actions based on provider state.
/// </summary>
public sealed class ProviderManagerViewModel : ReactiveViewModel
{
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(20);

    private readonly NetclawPaths _paths;
    private readonly ProviderDescriptorRegistry _registry;
    private readonly IProviderProbe _probe;
    private readonly DeviceFlowServiceFactory? _oauthFactory;
    private CancellationTokenSource? _probeCts;

    public ReactiveProperty<ProviderManagerState> CurrentState { get; } = new(ProviderManagerState.Loading);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);
    public ReactiveProperty<int> SpinnerTick { get; } = new(0);
    public ReactiveProperty<bool> IsEagerProbing { get; } = new(false);
    public ReactiveProperty<int> EagerProbeElapsedSeconds { get; } = new(0);

    /// <summary>
    /// Version counter for state changes that require DynamicLayoutNode invalidation.
    /// </summary>
    internal ReactiveProperty<int> StateVersion { get; } = new(0);

    // ── Display model ──
    public List<ProviderDisplayItem> DisplayProviders { get; } = [];
    public int SelectedProviderIndex { get; set; }

    /// <summary>
    /// The provider currently being viewed in Details or FixCredentials state.
    /// </summary>
    public ProviderDisplayItem? DetailProvider { get; set; }

    /// <summary>
    /// When true, validation after fix-credentials returns to List (not AddComplete).
    /// </summary>
    public bool IsFixFlow { get; set; }

    // ── Add flow state ──
    public string? NewProviderName { get; set; }
    public string? NewProviderType { get; set; }
    public AuthMethod NewAuthMethod { get; set; } = AuthMethod.None;
    public string? NewApiKey { get; set; }
    public string? NewEndpoint { get; set; }

    // ── OAuth flow (shared coordinator) ──
    public OAuthFlowCoordinator OAuth { get; private set; } = null!; // initialized in constructor

    // ── Fix flow state ──
    public string? FixApiKey { get; set; }
    public string? FixEndpoint { get; set; }

    // ── Remove flow state ──
    public string? RemoveProviderName { get; set; }
    public List<string> RemoveBlockingRoles { get; } = [];

    /// <summary>
    /// Completes when the provider probe finishes. Used for testing.
    /// </summary>
    internal Task? ProbeCompletion { get; private set; }

    /// <summary>
    /// Completes when the eager probe finishes. Used for testing.
    /// </summary>
    internal Task? EagerProbeCompletion { get; private set; }

    /// <summary>
    /// The provider descriptor registry. Exposed for use by the page.
    /// </summary>
    public ProviderDescriptorRegistry Registry => _registry;

    public ProviderManagerViewModel(NetclawPaths paths, ProviderDescriptorRegistry registry,
        DeviceFlowServiceFactory? oauthFactory = null, DaemonApi? daemonApi = null)
        : this(paths, registry, registry, oauthFactory, daemonApi)
    {
    }

    public ProviderManagerViewModel(NetclawPaths paths, ProviderDescriptorRegistry registry, IProviderProbe probe,
        DeviceFlowServiceFactory? oauthFactory = null, DaemonApi? daemonApi = null)
    {
        _paths = paths;
        _registry = registry;
        _probe = probe;
        _oauthFactory = oauthFactory;
        OAuth = new OAuthFlowCoordinator(
            registry,
            oauthFactory,
            daemonApi,
            requestRedraw: () => NotifyStateChanged());
    }

    public override void OnActivated()
    {
        base.OnActivated();
        RefreshDisplayProviders();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleGlobalKey)
            .DisposeWith(Subscriptions);

        // Start eager probing of configured providers
        EagerProbeCompletion = ProbeAllConfiguredAsync();
    }

    /// <summary>
    /// Build DisplayProviders from known types merged with loaded config.
    /// </summary>
    public void RefreshDisplayProviders()
    {
        DisplayProviders.Clear();
        var loaded = Provider.ProviderCommand.LoadProviders(_paths);

        foreach (var typeKey in _registry.KnownTypeKeys)
        {
            var descriptor = _registry.Get(typeKey);

            // Find configured provider of this type
            var configured = loaded
                .FirstOrDefault(p => string.Equals(p.Value.Type, typeKey, StringComparison.OrdinalIgnoreCase));

            if (configured.Key is not null)
            {
                var authStr = configured.Value.AuthMethod == AuthMethod.None
                    ? "\u2014"
                    : configured.Value.AuthMethod.ToString();

                DisplayProviders.Add(new ProviderDisplayItem
                {
                    ProviderType = typeKey,
                    IsConfigured = true,
                    ConfiguredName = configured.Key,
                    Entry = configured.Value,
                    DisplayEndpoint = configured.Value.Endpoint,
                    DisplayAuth = authStr
                });
            }
            else
            {
                DisplayProviders.Add(new ProviderDisplayItem
                {
                    ProviderType = typeKey,
                    IsConfigured = false,
                    DisplayEndpoint = $"({descriptor.DefaultEndpoint})",
                    DisplayAuth = "\u2014"
                });
            }
        }
    }

    /// <summary>
    /// Probe all configured providers concurrently on activation.
    /// </summary>
    internal async Task ProbeAllConfiguredAsync()
    {
        var configuredItems = DisplayProviders.Where(p => p.IsConfigured).ToList();
        if (configuredItems.Count == 0)
        {
            CurrentState.Value = ProviderManagerState.List;
            NotifyStateChanged();
            return;
        }

        IsEagerProbing.Value = true;
        EagerProbeElapsedSeconds.Value = 0;

        foreach (var item in configuredItems)
            item.Health = ProviderHealthStatus.Probing;

        NotifyStateChanged();

        using var timerCts = new CancellationTokenSource();
        _ = RunEagerProbeTimerAsync(timerCts.Token);

        var probeTasks = configuredItems.Select(async item =>
        {
            try
            {
                var result = item.Entry is not null
                    ? await _probe.ProbeAsync(item.Entry, CancellationToken.None)
                    : await _probe.ProbeAsync(item.ProviderType, item.Entry?.Endpoint,
                        GetProbeCredential(item.Entry), CancellationToken.None);

                item.ProbeResult = result;
                item.Health = result.Success
                    ? ProviderHealthStatus.Healthy
                    : ProviderHealthStatus.Unhealthy;
            }
            catch
            {
                item.Health = ProviderHealthStatus.Unhealthy;
            }

            NotifyStateChanged();
        });

        await Task.WhenAll(probeTasks);

        timerCts.Cancel();
        IsEagerProbing.Value = false;
        CurrentState.Value = ProviderManagerState.List;
        NotifyStateChanged();
    }

    private async Task RunEagerProbeTimerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }

            EagerProbeElapsedSeconds.Value++;
            RequestRedraw();
        }
    }

    /// <summary>
    /// Activate the currently selected provider based on its state.
    /// </summary>
    public void ActivateSelectedProvider()
    {
        if (SelectedProviderIndex < 0 || SelectedProviderIndex >= DisplayProviders.Count)
            return;

        var item = DisplayProviders[SelectedProviderIndex];

        if (!item.IsConfigured)
        {
            StartAddForType(item.ProviderType);
            return;
        }

        switch (item.Health)
        {
            case ProviderHealthStatus.Healthy:
                DetailProvider = item;
                CurrentState.Value = ProviderManagerState.Details;
                NotifyStateChanged();
                break;

            case ProviderHealthStatus.Unhealthy:
                StartFixCredentials(item);
                break;

            // Probing or Unchecked — no-op
        }
    }

    /// <summary>
    /// Start the add flow for a specific provider type (skips type selection).
    /// </summary>
    public void StartAddForType(string type)
    {
        ClearAddState();
        NewProviderType = type;
        NewProviderName = GenerateProviderName(type);

        var descriptor = _registry.Get(type);
        if (descriptor.Auth.SupportedAuthMethods is [AuthMethod.None])
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
    /// Start the fix-credentials flow for an unhealthy provider.
    /// </summary>
    public void StartFixCredentials(ProviderDisplayItem item)
    {
        DetailProvider = item;
        FixApiKey = null;
        FixEndpoint = item.Entry?.Endpoint;
        IsFixFlow = true;
        CurrentState.Value = ProviderManagerState.FixCredentials;
        NotifyStateChanged();
    }

    /// <summary>
    /// Start OAuth re-authentication for an OAuth-only provider in fix-credentials flow.
    /// Sets up fix-flow state and delegates to <see cref="SelectAuthMethod"/>.
    /// </summary>
    public void StartOAuthReAuth()
    {
        if (DetailProvider is null) return;

        var type = DetailProvider.ProviderType;
        var descriptor = _registry.Get(type);

        NewProviderType = type;
        NewProviderName = DetailProvider.ConfiguredName;
        IsFixFlow = true;

        var oauthMethod = descriptor.Auth.SupportedAuthMethods
            .FirstOrDefault(m => m is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice);

        SelectAuthMethod(oauthMethod);
    }

    /// <summary>
    /// Select auth method and advance to credential input or OAuth device flow.
    /// </summary>
    public void SelectAuthMethod(AuthMethod method)
    {
        NewAuthMethod = method;

        if (method == AuthMethod.OAuthDevice)
        {
            CurrentState.Value = ProviderManagerState.AddOAuthDeviceFlow;
            NotifyStateChanged();
            ProbeElapsedSeconds.Value = 0;
            var ct = OAuth.StartDeviceFlow(NewProviderType!, result =>
            {
                NewApiKey = result.AccessToken.Value;
                CurrentState.Value = ProviderManagerState.AddValidating;
                NotifyStateChanged();
                StartProbe();
            });
            _ = RunProbeTimerAsync(ct);
            return;
        }

        if (method == AuthMethod.OAuthPkce)
        {
            CurrentState.Value = ProviderManagerState.AddBrowserOAuthFlow;
            NotifyStateChanged();
            ProbeElapsedSeconds.Value = 0;
            var ct = OAuth.StartBrowserFlow(NewProviderType!, result =>
            {
                NewApiKey = result.AccessToken.Value;
                NewAuthMethod = AuthMethod.OAuthPkce;
                CurrentState.Value = ProviderManagerState.AddValidating;
                NotifyStateChanged();
                StartProbe();
            });
            _ = RunProbeTimerAsync(ct);
            return;
        }

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
    /// Submit fixed credentials and start validation probe.
    /// </summary>
    public void SubmitFixCredentials()
    {
        if (DetailProvider is null) return;

        var type = DetailProvider.ProviderType;
        var descriptor = _registry.Get(type);

        if (descriptor.Auth.SupportedAuthMethods.Contains(AuthMethod.ApiKey) && string.IsNullOrWhiteSpace(FixApiKey))
        {
            StatusMessage.Value = "API key is required.";
            RequestRedraw();
            return;
        }

        // Write updated credentials
        if (DetailProvider.ConfiguredName is not null)
        {
            if (!string.IsNullOrWhiteSpace(FixApiKey))
            {
                var (_, secrets) = ConfigFileHelper.LoadConfigFiles(_paths);
                var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
                secretProviders[DetailProvider.ConfiguredName] = new Dictionary<string, object>
                {
                    ["ApiKey"] = FixApiKey
                };
                ConfigFileHelper.WriteSecretsFile(_paths, secrets);
            }

            if (FixEndpoint is not null && DetailProvider.Entry is not null
                && !string.Equals(FixEndpoint, DetailProvider.Entry.Endpoint, StringComparison.Ordinal))
            {
                var (config, _) = ConfigFileHelper.LoadConfigFiles(_paths);
                var providers = ConfigFileHelper.GetOrCreateSection(config, "Providers");
                if (providers.TryGetValue(DetailProvider.ConfiguredName, out var existing) &&
                    existing is Dictionary<string, object> providerDict)
                {
                    providerDict["Endpoint"] = FixEndpoint;
                    ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
                }
            }
        }

        // Set up probe using fix credentials
        NewProviderType = type;
        NewEndpoint = FixEndpoint;
        NewApiKey = FixApiKey
            ?? DetailProvider.Entry?.ApiKey?.Value
            ?? DetailProvider.Entry?.OAuthAccessToken?.Value;
        IsFixFlow = true;

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
        StatusMessage.Value = $"Added provider '{NewProviderName}'. Restart daemon for changes to take effect.";
        ClearAddState();
        RefreshAndProbeAll();
    }

    /// <summary>
    /// Start remove confirmation for the detail provider.
    /// </summary>
    public void StartRemove()
    {
        if (DetailProvider is not { IsConfigured: true, ConfiguredName: not null })
            return;

        RemoveProviderName = DetailProvider.ConfiguredName;
        RemoveBlockingRoles.Clear();

        var roles = Provider.ProviderCommand.GetReferencingModelRoles(RemoveProviderName, _paths);
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
            ConfigFileHelper.WriteSecretsFile(_paths, secrets);

        StatusMessage.Value = $"Removed provider '{RemoveProviderName}'. Restart daemon for changes to take effect.";
        RemoveProviderName = null;
        DetailProvider = null;
        RefreshAndProbeAll();
    }

    /// <summary>
    /// Re-probe the detail provider inline from the Details state.
    /// </summary>
    public void RevalidateDetailProvider()
    {
        if (DetailProvider is not { IsConfigured: true, Entry: not null })
            return;

        DetailProvider.Health = ProviderHealthStatus.Probing;
        NotifyStateChanged();

        _ = RevalidateAsync(DetailProvider);
    }

    private async Task RevalidateAsync(ProviderDisplayItem item)
    {
        try
        {
            var result = item.Entry is not null
                ? await _probe.ProbeAsync(item.Entry, CancellationToken.None)
                : await _probe.ProbeAsync(item.ProviderType, item.Entry?.Endpoint,
                    GetProbeCredential(item.Entry), CancellationToken.None);

            item.ProbeResult = result;
            item.Health = result.Success
                ? ProviderHealthStatus.Healthy
                : ProviderHealthStatus.Unhealthy;
        }
        catch
        {
            item.Health = ProviderHealthStatus.Unhealthy;
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Handle a pasted redirect URL for browser OAuth fallback.
    /// </summary>
    public async Task SubmitRedirectUrlAsync(string? pastedUrl)
    {
        await OAuth.SubmitRedirectUrlAsync(pastedUrl);
        if (OAuth.FlowState.Value == DeviceFlowState.Succeeded)
        {
            CurrentState.Value = ProviderManagerState.AddValidating;
            NotifyStateChanged();
            StartProbe();
        }
    }

    public void GoBackToList()
    {
        CancelProbe();
        ClearAddState();
        DetailProvider = null;
        IsFixFlow = false;
        FixApiKey = null;
        FixEndpoint = null;
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
            case ProviderManagerState.AddSelectAuth:
                GoBackToList();
                break;
            case ProviderManagerState.AddOAuthDeviceFlow:
            case ProviderManagerState.AddBrowserOAuthFlow:
                OAuth.Cancel();
                CurrentState.Value = ProviderManagerState.AddSelectAuth;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddCredentials:
                var descriptor = _registry.Get(NewProviderType ?? "");
                if (descriptor.Auth.SupportedAuthMethods is [AuthMethod.None])
                    GoBackToList();
                else
                    CurrentState.Value = ProviderManagerState.AddSelectAuth;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddValidating:
                CancelProbe();
                if (IsFixFlow)
                {
                    CurrentState.Value = ProviderManagerState.FixCredentials;
                }
                else
                {
                    CurrentState.Value = ProviderManagerState.AddCredentials;
                }
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddComplete:
                GoBackToList();
                break;
            case ProviderManagerState.Details:
            case ProviderManagerState.FixCredentials:
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

    /// <summary>
    /// Refresh the display list from config and re-probe all configured providers.
    /// Transitions through Loading → List with health indicators.
    /// Preserves <see cref="StatusMessage"/> so callers can set it before calling.
    /// </summary>
    internal void RefreshAndProbeAll()
    {
        RefreshDisplayProviders();
        CurrentState.Value = ProviderManagerState.Loading;
        NotifyStateChanged();
        EagerProbeCompletion = ProbeAllConfiguredAsync();
    }

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
        var providerType = NewProviderType ?? "unknown";
        var probeId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        Exception? probeException = null;

        IsProbing.Value = true;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        RequestRedraw();

        ProbeDiagnosticsLog.Write(
            _paths,
            "provider-manager",
            providerType,
            NewEndpoint,
            probeId,
            "start");

        _ = RunProbeTimerAsync(ct);

        var result = new ProviderProbeResult(false, "Validation failed before probe completed.", []);
        try
        {
            result = await _probe.ProbeAsync(
                    providerType,
                    NewEndpoint,
                    NewApiKey,
                    NewAuthMethod,
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
            CancelProbe();

            ProbeDiagnosticsLog.Write(
                _paths,
                "provider-manager",
                providerType,
                NewEndpoint,
                probeId,
                result.Success ? "success" : "failure",
                result.ErrorMessage,
                stopwatch.Elapsed,
                probeException);
        }

        IsProbing.Value = false;
        ProbeResult.Value = result;

        if (result.Success)
        {
            if (IsFixFlow)
            {
                // Fix flow: re-probe all providers so list shows fresh health
                IsFixFlow = false;
                StatusMessage.Value = "Credentials updated successfully. Restart daemon for changes to take effect.";
                RefreshAndProbeAll();
            }
            else
            {
                CurrentState.Value = ProviderManagerState.AddComplete;
            }
        }

        NotifyStateChanged();
    }

    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        var tickCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(120, ct); }
            catch (OperationCanceledException) { return; }

            tickCount++;
            SpinnerTick.Value = tickCount;

            if (tickCount % 8 == 0)
                ProbeElapsedSeconds.Value++;

            RequestRedraw();
        }
    }


    // ── Config writing ──

    private void WriteProviderConfig()
    {
        ProviderCredentialWriter.WriteProvider(
            _paths,
            NewProviderName!,
            NewProviderType!,
            NewAuthMethod,
            NewEndpoint,
            OAuth.Result,
            NewApiKey,
            _registry);
    }

    // ── Helpers ──

    private static string? GetProbeCredential(ProviderEntry? entry)
        => entry?.ApiKey?.Value ?? entry?.OAuthAccessToken?.Value;

    private string GenerateProviderName(string type)
    {
        var baseName = $"my-{type}";
        if (!DisplayProviders.Any(p =>
            p.ConfiguredName is not null &&
            string.Equals(p.ConfiguredName, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"my-{type}-{i}";
            if (!DisplayProviders.Any(p =>
                p.ConfiguredName is not null &&
                string.Equals(p.ConfiguredName, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        return $"my-{type}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    private void ClearAddState()
    {
        CancelProbe();
        OAuth.Reset();
        NewProviderName = null;
        NewProviderType = null;
        NewAuthMethod = AuthMethod.None;
        NewApiKey = null;
        NewEndpoint = null;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        IsFixFlow = false;
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
        OAuth.Dispose();
        CurrentState.Dispose();
        StatusMessage.Dispose();
        IsProbing.Dispose();
        ProbeResult.Dispose();
        ProbeElapsedSeconds.Dispose();
        SpinnerTick.Dispose();
        IsEagerProbing.Dispose();
        EagerProbeElapsedSeconds.Dispose();
        StateVersion.Dispose();
        base.Dispose();
    }
}
