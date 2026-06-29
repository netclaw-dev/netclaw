// -----------------------------------------------------------------------
// <copyright file="ProviderManagerViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.GitHubCopilot;
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
    AddSelectType,
    AddName,
    AddSelectAuth,
    AddGitHubCopilotAuthHost,
    AddGitHubCopilotEnterpriseHost,
    AddGitHubCopilotEnterpriseApiBase,
    AddCredentials,
    AddOAuthDeviceFlow,
    AddBrowserOAuthFlow,
    AddValidating,
    AddComplete,
    Details,
    RenameProvider,
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
    public string DisplayName { get; init; } = "";
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

    private readonly NetclawPaths _paths;
    private readonly ProviderDescriptorRegistry _registry;
    private readonly IProviderProbe _probe;
    private readonly DeviceFlowServiceFactory? _oauthFactory;
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _revalidateCts;

    internal Action<string>? RouteRequested { get; set; }

    /// <summary>
    /// True when this manager is hosted inside <c>netclaw config</c> (reached from the dashboard).
    /// Set by the embedded host registration; left false for the standalone <c>netclaw provider</c>
    /// host. Controls whether backing out past the root navigates to the dashboard or exits the app.
    /// </summary>
    internal bool IsEmbeddedInConfig { get; set; }

    public ReactiveProperty<ProviderManagerState> CurrentState { get; } = new(ProviderManagerState.Loading);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<string> ErrorMessage { get; } = new("");
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);
    public ReactiveProperty<bool> IsEagerProbing { get; } = new(false);

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
    public IReadOnlyDictionary<string, object?>? NewVendorOptions { get; set; }
    public GitHubCopilotAuthHostMode NewGitHubCopilotHostMode { get; set; } = GitHubCopilotAuthHostMode.GitHubCom;
    public string? NewGitHubCopilotHost { get; set; }
    public string? NewGitHubCopilotApiBase { get; set; }
    private bool _newProviderPersisted;

    // ── OAuth flow (shared coordinator) ──
    public OAuthFlowCoordinator OAuth { get; private set; } = null!; // initialized in constructor

    // ── Fix flow state ──
    public string? FixApiKey { get; set; }
    public string? FixEndpoint { get; set; }

    // ── Remove flow state ──
    public string? RemoveProviderName { get; set; }
    public List<string> RemoveBlockingRoles { get; } = [];

    // ── Rename flow state ──
    public string? RenameNewName { get; set; }

    /// <summary>
    /// Completes when the provider probe finishes. Used for testing.
    /// </summary>
    internal Task? ProbeCompletion { get; private set; }

    /// <summary>
    /// Completes when the eager probe finishes. Used for testing.
    /// </summary>
    internal Task? EagerProbeCompletion { get; private set; }

    /// <summary>
    /// Completes when the detail-provider revalidation finishes. Used for testing.
    /// </summary>
    internal Task? RevalidateCompletion { get; private set; }

    /// <summary>
    /// The provider descriptor registry. Exposed for use by the page.
    /// </summary>
    public ProviderDescriptorRegistry Registry => _registry;

    public ProviderManagerViewModel(NetclawPaths paths, ProviderDescriptorRegistry registry,
        DeviceFlowServiceFactory? oauthFactory = null, DaemonApi? daemonApi = null,
        EmbeddedConfigHostMarker? embeddedHost = null)
        : this(paths, registry, registry, oauthFactory, daemonApi, embeddedHost)
    {
    }

    public ProviderManagerViewModel(NetclawPaths paths, ProviderDescriptorRegistry registry, IProviderProbe probe,
        DeviceFlowServiceFactory? oauthFactory = null, DaemonApi? daemonApi = null,
        EmbeddedConfigHostMarker? embeddedHost = null)
    {
        _paths = paths;
        _registry = registry;
        _probe = probe;
        _oauthFactory = oauthFactory;
        IsEmbeddedInConfig = embeddedHost is not null;
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
    /// Shows all configured instances (including multiple of the same type),
    /// followed by unconfigured type placeholders for types with no instances.
    /// </summary>
    public void RefreshDisplayProviders()
    {
        DisplayProviders.Clear();
        var loaded = Provider.ProviderCommand.LoadProviders(_paths);

        // Pass 1: Add all configured instances
        var configuredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in loaded.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            configuredTypes.Add(entry.Type);
            var displayName = _registry.TryGet(entry.Type, out var descriptor)
                ? descriptor.DisplayName
                : entry.Type;

            var authStr = entry.AuthMethod == AuthMethod.None
                ? "\u2014"
                : entry.AuthMethod.ToString();

            DisplayProviders.Add(new ProviderDisplayItem
            {
                ProviderType = entry.Type,
                DisplayName = displayName,
                IsConfigured = true,
                ConfiguredName = name,
                Entry = entry,
                DisplayEndpoint = entry.Endpoint,
                DisplayAuth = authStr
            });
        }

        // Pass 2: Add unconfigured placeholders for types with no instances
        foreach (var typeKey in _registry.KnownTypeKeys)
        {
            if (configuredTypes.Contains(typeKey))
                continue;

            var descriptor = _registry.Get(typeKey);
            DisplayProviders.Add(new ProviderDisplayItem
            {
                ProviderType = typeKey,
                DisplayName = descriptor.DisplayName,
                IsConfigured = false,
                DisplayEndpoint = $"({descriptor.DefaultEndpoint})",
                DisplayAuth = "\u2014"
            });
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

        foreach (var item in configuredItems)
            item.Health = ProviderHealthStatus.Probing;

        NotifyStateChanged();

        // Each provider's completion calls NotifyStateChanged() to refresh its
        // health glyph; the in-progress spinners self-animate via SpinnerNode, so
        // no eager-probe redraw timer is needed.
        var probeTasks = configuredItems.Select(async item =>
        {
            try
            {
                var result = await ProbeDisplayItemAsync(item, CancellationToken.None);

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

        IsEagerProbing.Value = false;
        CurrentState.Value = ProviderManagerState.List;
        NotifyStateChanged();
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
    /// Start the add-new-provider flow by showing the type selection screen.
    /// Called when the user selects the "+ Add new provider" sentinel row.
    /// </summary>
    public void StartAddNewProvider()
    {
        ClearAddState();
        CurrentState.Value = ProviderManagerState.AddSelectType;
        NotifyStateChanged();
    }

    /// <summary>
    /// Start the add flow for a specific provider type. Enters the
    /// <see cref="ProviderManagerState.AddName"/> step first so the user
    /// can confirm or override the auto-generated provider name before
    /// any credential entry happens.
    /// </summary>
    public void StartAddForType(string type)
    {
        ClearAddState();
        NewProviderType = type;
        NewProviderName = GenerateProviderName(type);

        CurrentState.Value = ProviderManagerState.AddName;
        NotifyStateChanged();
    }

    /// <summary>
    /// Advance past the <see cref="ProviderManagerState.AddName"/> step into
    /// the auth/credentials portion of the add flow. Routes to
    /// <see cref="ProviderManagerState.AddCredentials"/> directly for
    /// endpoint-only providers (where there's nothing to authenticate),
    /// otherwise to <see cref="ProviderManagerState.AddSelectAuth"/>.
    /// </summary>
    public void AdvanceAfterName()
    {
        if (NewProviderType is null) return;

        var descriptor = _registry.Get(NewProviderType);
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
    /// Validate and apply a user-supplied provider name. Returns true on
    /// success (sets <see cref="NewProviderName"/> to the trimmed input).
    /// Returns false and populates <paramref name="error"/> on rejection.
    /// </summary>
    /// <remarks>
    /// Validation matches the existing collision check in
    /// <see cref="GenerateProviderName"/> (case-insensitive comparison against
    /// other configured providers). The config schema treats Providers as
    /// open-keyed (additionalProperties: true with no propertyNames pattern),
    /// so we don't enforce slug rules here — just non-empty and unique.
    /// </remarks>
    public bool TrySetNewProviderName(string? proposed, out string error)
    {
        var trimmed = proposed?.Trim() ?? string.Empty;

        // Persist the candidate on both success and failure so a redraw
        // triggered by ErrorMessage doesn't wipe out the user's input.
        NewProviderName = trimmed;

        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Provider name cannot be empty.";
            return false;
        }

        foreach (var existing in DisplayProviders)
        {
            if (existing.ConfiguredName is not null &&
                string.Equals(existing.ConfiguredName, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                error = $"A provider named '{existing.ConfiguredName}' already exists.";
                return false;
            }
        }

        error = string.Empty;
        return true;
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
        NewEndpoint = DetailProvider.Entry?.Endpoint;
        NewVendorOptions = string.Equals(type, "github-copilot", StringComparison.OrdinalIgnoreCase)
                           && DetailProvider.Entry is not null
            ? GitHubCopilotAuthResolver.ToVendorOptions(DetailProvider.Entry)
            : null;
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
            if (!IsFixFlow && GitHubCopilotSetupFlow.IsGitHubCopilot(NewProviderType))
            {
                CurrentState.Value = ProviderManagerState.AddGitHubCopilotAuthHost;
                NotifyStateChanged();
                return;
            }

            StartOAuthDeviceFlow();
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

    public void SelectGitHubCopilotAuthHost(GitHubCopilotAuthHostMode mode)
    {
        NewGitHubCopilotHostMode = mode;
        ErrorMessage.Value = "";

        if (mode == GitHubCopilotAuthHostMode.GitHubCom)
        {
            NewGitHubCopilotHost = null;
            NewGitHubCopilotApiBase = null;
            NewVendorOptions = null;
            StartOAuthDeviceFlow();
            return;
        }

        CurrentState.Value = ProviderManagerState.AddGitHubCopilotEnterpriseHost;
        NotifyStateChanged();
    }

    public bool TrySetGitHubCopilotEnterpriseHost(string? gitHubHost, out string error)
    {
        var previousHost = NewGitHubCopilotHost;
        var trimmedHost = gitHubHost?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedHost))
        {
            error = "GitHub Enterprise host is required.";
            return false;
        }

        if (!GitHubCopilotSetupFlow.TryResolveEnterpriseVendorOptions(
                trimmedHost,
                gitHubApiBase: null,
                out _,
                out error))
        {
            return false;
        }

        NewGitHubCopilotHost = trimmedHost;
        if (!string.Equals(previousHost, trimmedHost, StringComparison.Ordinal))
        {
            NewGitHubCopilotApiBase = null;
            NewVendorOptions = null;
        }

        error = string.Empty;
        return true;
    }

    public bool SubmitGitHubCopilotEnterpriseHost(string? gitHubHost, out string error)
    {
        if (!TrySetGitHubCopilotEnterpriseHost(gitHubHost, out error))
            return false;

        CurrentState.Value = ProviderManagerState.AddGitHubCopilotEnterpriseApiBase;
        NotifyStateChanged();
        return true;
    }

    public bool TryStartGitHubCopilotEnterpriseOAuth(string? gitHubApiBase, out string error)
    {
        NewGitHubCopilotApiBase = string.IsNullOrWhiteSpace(gitHubApiBase)
            ? null
            : gitHubApiBase.Trim();

        if (!GitHubCopilotSetupFlow.TryResolveEnterpriseVendorOptions(
                NewGitHubCopilotHost,
                NewGitHubCopilotApiBase,
                out var vendorOptions,
                out error))
        {
            return false;
        }

        NewVendorOptions = vendorOptions;
        ErrorMessage.Value = "";
        StartOAuthDeviceFlow();
        return true;
    }

    private void StartOAuthDeviceFlow()
    {
        if (!TryBuildOAuthFlowEntry(out var oauthEntry, out var error))
        {
            ErrorMessage.Value = error;
            RequestRedraw();
            return;
        }

        CurrentState.Value = ProviderManagerState.AddOAuthDeviceFlow;
        NotifyStateChanged();
        ProbeElapsedSeconds.Value = 0;
        var ct = OAuth.StartDeviceFlow(NewProviderType!, result =>
        {
            NewApiKey = result.AccessToken.Value;
            CurrentState.Value = ProviderManagerState.AddValidating;
            NotifyStateChanged();
            StartProbe();
        }, oauthEntry);
        _ = RunProbeTimerAsync(ct);
    }

    private bool TryBuildOAuthFlowEntry(out ProviderEntry? entry, out string error)
    {
        entry = null;
        error = string.Empty;
        if (!GitHubCopilotSetupFlow.IsGitHubCopilot(NewProviderType))
            return true;

        if (IsFixFlow && DetailProvider?.Entry is { } existing)
        {
            entry = existing;
            return true;
        }

        entry = GitHubCopilotSetupFlow.BuildOAuthEntry(NewVendorOptions);
        return true;
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

        // Do NOT write the new credential yet: defer the secrets/config write to the probe-success
        // branch (WriteFixedCredentials) so a bad API key or endpoint never clobbers the working one
        // on disk with no rollback. The normal add flow defers its write identically.
        NewProviderType = type;
        NewEndpoint = FixEndpoint;
        NewApiKey = FixApiKey
            ?? DetailProvider.Entry?.ApiKey?.Value
            ?? DetailProvider.Entry?.OAuthAccessToken?.Value;
        NewVendorOptions = string.Equals(type, "github-copilot", StringComparison.OrdinalIgnoreCase)
                           && DetailProvider.Entry is not null
            ? GitHubCopilotAuthResolver.ToVendorOptions(DetailProvider.Entry)
            : null;
        IsFixFlow = true;

        CurrentState.Value = ProviderManagerState.AddValidating;
        NotifyStateChanged();
        StartProbe();
    }

    // Persists the fixed API key (to secrets.json) and endpoint (to netclaw.json) for the provider
    // being repaired. Called only from the probe-success branch so an invalid new credential never
    // overwrites the working one. Updates the existing provider entry keyed by ConfiguredName.
    private void WriteFixedCredentials()
    {
        if (DetailProvider?.ConfiguredName is not { } name)
            return;

        if (!string.IsNullOrWhiteSpace(FixApiKey))
        {
            var (_, secrets) = ConfigFileHelper.LoadConfigFiles(_paths);
            var secretProviders = ConfigFileHelper.GetOrCreateSection(secrets, "Providers");
            secretProviders[name] = new Dictionary<string, object>
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
            if (providers.TryGetValue(name, out var existing) &&
                existing is Dictionary<string, object> providerDict)
            {
                providerDict["Endpoint"] = FixEndpoint;
                ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
            }
        }
    }

    /// <summary>
    /// Finish a successful add flow and return to the refreshed provider list.
    /// </summary>
    public void ConfirmAdd()
    {
        if (!_newProviderPersisted)
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
    /// Begin a rename of the currently displayed Details provider.
    /// Pre-fills <see cref="RenameNewName"/> with the existing configured name.
    /// </summary>
    public void StartRename()
    {
        if (DetailProvider is not { IsConfigured: true, ConfiguredName: not null })
            return;

        RenameNewName = DetailProvider.ConfiguredName;
        ErrorMessage.Value = "";
        CurrentState.Value = ProviderManagerState.RenameProvider;
        NotifyStateChanged();
    }

    /// <summary>
    /// Apply the proposed rename. Validates and delegates the key swap to
    /// <see cref="Provider.ProviderRenamer"/>. On success, refreshes the
    /// provider list and returns to it. On failure, sets
    /// <see cref="ErrorMessage"/> and stays on the rename page.
    /// </summary>
    public void ConfirmRename(string? proposed)
    {
        if (DetailProvider is not { ConfiguredName: { } oldName })
            return;

        var trimmed = proposed?.Trim() ?? string.Empty;

        // Persist the candidate so a redraw triggered by ErrorMessage doesn't
        // wipe out the user's input on the validation-failure path below.
        RenameNewName = trimmed;

        // Exact match is a no-op so the user can re-confirm without writing.
        // Case-only edits (e.g. "my-vllm" → "My-Vllm") fall through to the
        // renamer, which rewrites the key in place.
        if (string.Equals(trimmed, oldName, StringComparison.Ordinal))
        {
            RenameNewName = null;
            CurrentState.Value = ProviderManagerState.Details;
            NotifyStateChanged();
            return;
        }

        var result = Provider.ProviderRenamer.Rename(_paths, oldName, trimmed);
        if (!result.Success)
        {
            ErrorMessage.Value = result.ErrorMessage ?? "Rename failed.";
            RequestRedraw();
            return;
        }

        StatusMessage.Value = result.ReassignedModelRoles.Count > 0
            ? $"Renamed '{oldName}' to '{trimmed}'. Reassigned model role(s): {string.Join(", ", result.ReassignedModelRoles)}. Restart daemon for changes to take effect."
            : $"Renamed '{oldName}' to '{trimmed}'. Restart daemon for changes to take effect.";

        RenameNewName = null;
        DetailProvider = null;
        RefreshAndProbeAll();
    }

    /// <summary>
    /// Cancel an in-progress rename and return to the Details view.
    /// </summary>
    public void CancelRename()
    {
        RenameNewName = null;
        ErrorMessage.Value = "";
        if (DetailProvider is not null)
        {
            CurrentState.Value = ProviderManagerState.Details;
            NotifyStateChanged();
        }
        else
        {
            GoBackToList();
        }
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

        CancelRevalidate();
        _revalidateCts = new CancellationTokenSource();
        RevalidateCompletion = RevalidateAsync(DetailProvider, _revalidateCts.Token);
    }

    // Cancel and dispose the in-flight detail-provider revalidation. Called when a newer revalidate
    // starts, when the operator leaves the detail view, and on dispose — all on the UI thread.
    private void CancelRevalidate()
    {
        if (_revalidateCts is not null)
        {
            _revalidateCts.Cancel();
            _revalidateCts.Dispose();
            _revalidateCts = null;
        }
    }

    private async Task RevalidateAsync(ProviderDisplayItem item, CancellationToken ct)
    {
        try
        {
            var result = await ProbeDisplayItemAsync(item, ct);

            // Abandoned (operator left the detail view, or a newer revalidate started): do not
            // update health or redraw against a stale/disposed view-model.
            if (ct.IsCancellationRequested)
                return;

            item.ProbeResult = result;
            item.Health = result.Success
                ? ProviderHealthStatus.Healthy
                : ProviderHealthStatus.Unhealthy;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            if (ct.IsCancellationRequested)
                return;

            item.Health = ProviderHealthStatus.Unhealthy;
        }

        NotifyStateChanged();
    }

    private Task<ProviderProbeResult> ProbeDisplayItemAsync(
        ProviderDisplayItem item,
        CancellationToken ct)
    {
        // Only persisted providers have a stable config key for refresh writes;
        // pending add/fix entries must stay on the no-clobber probe path.
        if (item is { Entry: not null, ConfiguredName: not null }
            && _probe is IConfiguredProviderProbe configuredProbe)
        {
            return configuredProbe.ProbeConfiguredAsync(item.ConfiguredName, item.Entry, ct);
        }

        return item.Entry is not null
            ? _probe.ProbeAsync(item.Entry, ct)
            : _probe.ProbeAsync(item.ProviderType, item.Entry?.Endpoint,
                GetProbeCredential(item.Entry), ct);
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
        CancelRevalidate();
        ClearAddState();
        DetailProvider = null;
        IsFixFlow = false;
        FixApiKey = null;
        FixEndpoint = null;
        RemoveProviderName = null;
        RemoveBlockingRoles.Clear();
        RenameNewName = null;
        StatusMessage.Value = "";
        ErrorMessage.Value = "";
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
            case ProviderManagerState.AddName:
                GoBackToList();
                break;
            case ProviderManagerState.AddSelectAuth:
                GoBackToList();
                break;
            case ProviderManagerState.AddGitHubCopilotAuthHost:
                CurrentState.Value = ProviderManagerState.AddSelectAuth;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddGitHubCopilotEnterpriseHost:
                CurrentState.Value = ProviderManagerState.AddGitHubCopilotAuthHost;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddGitHubCopilotEnterpriseApiBase:
                CurrentState.Value = ProviderManagerState.AddGitHubCopilotEnterpriseHost;
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddOAuthDeviceFlow:
            case ProviderManagerState.AddBrowserOAuthFlow:
                OAuth.Cancel();
                CurrentState.Value = GitHubCopilotSetupFlow.IsGitHubCopilot(NewProviderType) && !IsFixFlow
                    ? ProviderManagerState.AddGitHubCopilotAuthHost
                    : ProviderManagerState.AddSelectAuth;
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
                    CurrentState.Value = NewAuthMethod == AuthMethod.OAuthDevice
                                         && GitHubCopilotSetupFlow.IsGitHubCopilot(NewProviderType)
                        ? ProviderManagerState.AddOAuthDeviceFlow
                        : ProviderManagerState.AddCredentials;
                }
                NotifyStateChanged();
                break;
            case ProviderManagerState.AddComplete:
                ConfirmAdd();
                break;
            case ProviderManagerState.Details:
            case ProviderManagerState.FixCredentials:
                GoBackToList();
                break;
            case ProviderManagerState.RemoveConfirm:
                GoBackToList();
                break;
            case ProviderManagerState.RenameProvider:
                CancelRename();
                break;
            default:
                if (IsEmbeddedInConfig)
                {
                    // Embedded in `netclaw config`: return to the dashboard. We must NOT Shutdown
                    // here — Shutdown cancels the run loop's token before the queued navigation is
                    // processed, dropping the nav and quitting the entire config app.
                    RouteRequested?.Invoke("/config");
                    Navigate?.Invoke("/config");
                }
                else
                {
                    // Standalone `netclaw provider`: backing out past the root exits the app.
                    Shutdown();
                }

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
        var probeEntry = BuildNewProviderProbeEntry(providerType);
        var probeId = IdGen.ShortId();
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
            // Whole-probe wall-clock (covers pre-request work like OAuth token exchange);
            // ProbeTimeouts.InteractiveWallClock stays above the descriptor's per-request
            // deadline so it never truncates a legitimately slow self-hosted probe (#1292).
            result = await _probe.ProbeAsync(probeEntry, ct)
                .WaitAsync(ProbeTimeouts.InteractiveWallClock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ProviderProbeResult(false, "Validation cancelled.", []);
        }
        catch (TimeoutException)
        {
            result = new ProviderProbeResult(false,
                $"Validation timed out after {(int)ProbeTimeouts.InteractiveWallClock.TotalSeconds} seconds. Check network connectivity and try again.", []);
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
                if (NewProviderName is not null && OAuth.Result is not null)
                {
                    WriteProviderConfig();
                    _newProviderPersisted = true;
                }
                else
                {
                    // API-key / endpoint fix: persist only now that the probe succeeded, so a typo
                    // in the new credential leaves the prior working secret untouched on disk.
                    WriteFixedCredentials();
                }

                // Fix flow: re-probe all providers so list shows fresh health
                IsFixFlow = false;
                StatusMessage.Value = "Credentials updated successfully. Restart daemon for changes to take effect.";
                RefreshAndProbeAll();
            }
            else
            {
                WriteProviderConfig();
                _newProviderPersisted = true;
                StatusMessage.Value = $"Added provider '{NewProviderName}'. Restart daemon for changes to take effect.";
                CurrentState.Value = ProviderManagerState.AddComplete;
            }
        }

        NotifyStateChanged();
    }

    // Drives only the cosmetic "(Ns)" elapsed counter now that the spinner glyph
    // self-animates via SpinnerNode; a 1 Hz tick is all that's needed.
    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }

            ProbeElapsedSeconds.Value++;
        }
    }

    private ProviderEntry BuildNewProviderProbeEntry(string providerType)
    {
        var entry = new ProviderEntry
        {
            Type = providerType,
            Endpoint = NewEndpoint ?? "",
            AuthMethod = NewAuthMethod
        };

        if (NewAuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce)
        {
            var result = OAuth.Result;
            var credential = NewApiKey;
            if (string.IsNullOrWhiteSpace(credential))
                credential = result?.AccessToken.Value;

            entry.OAuthAccessToken = !string.IsNullOrWhiteSpace(credential)
                ? new SensitiveString(credential)
                : null;
            entry.OAuthRefreshToken = result?.RefreshToken;
            entry.OAuthTokenExpiry = result?.ExpiresAt;
            entry.OAuthAccountId = result?.AccountId;
        }
        else if (!string.IsNullOrWhiteSpace(NewApiKey))
        {
            entry.ApiKey = new SensitiveString(NewApiKey);
        }

        entry.SetVendorOptions(ToJsonObject(NewVendorOptions));

        return entry;
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
            _registry,
            vendorOptions: NewVendorOptions);
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

        return $"my-{type}-{IdGen.Suffix()}";
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
        NewVendorOptions = null;
        NewGitHubCopilotHostMode = GitHubCopilotAuthHostMode.GitHubCom;
        NewGitHubCopilotHost = null;
        NewGitHubCopilotApiBase = null;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        IsFixFlow = false;
        _newProviderPersisted = false;
    }

    private static JsonObject? ToJsonObject(IReadOnlyDictionary<string, object?>? vendorOptions)
    {
        if (vendorOptions is null || vendorOptions.Count == 0)
            return null;

        return JsonNode.Parse(JsonSerializer.Serialize(vendorOptions, JsonDefaults.ConfigFile))?.AsObject();
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
        CancelRevalidate();
        OAuth.Dispose();
        CurrentState.Dispose();
        StatusMessage.Dispose();
        ErrorMessage.Dispose();
        IsProbing.Dispose();
        ProbeResult.Dispose();
        ProbeElapsedSeconds.Dispose();
        IsEagerProbing.Dispose();
        StateVersion.Dispose();
        base.Dispose();
    }
}
