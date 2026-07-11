// -----------------------------------------------------------------------
// <copyright file="ModelManagerViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Diagnostics;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Providers;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// States for the model manager TUI.
/// </summary>
public enum ModelManagerState
{
    RoleOverview,
    SelectProvider,
    DiscoverModels,
    ConfirmAssignment
}

/// <summary>
/// Reactive ViewModel for the <c>netclaw model</c> interactive TUI.
/// Manages viewing model role assignments, discovering models, and assigning them.
/// </summary>
public sealed class ModelManagerViewModel : ReactiveViewModel
{
    private const int MaxDisplayedModels = 30;

    private readonly NetclawPaths _paths;
    private readonly IProviderProbe _probe;
    private readonly ProviderDescriptorRegistry? _registry;
    private CancellationTokenSource? _probeCts;

    internal Action<string>? RouteRequested { get; set; }

    /// <summary>
    /// True when this manager is hosted inside <c>netclaw config</c> (reached from the dashboard).
    /// Set by the embedded host registration; left false for the standalone <c>netclaw model</c>
    /// host. Controls whether backing out past the root navigates to the dashboard or exits the app.
    /// </summary>
    internal bool IsEmbeddedInConfig { get; set; }

    public ReactiveProperty<ModelManagerState> CurrentState { get; } = new(ModelManagerState.RoleOverview);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);

    /// <summary>
    /// Version counter for state changes that require DynamicLayoutNode invalidation.
    /// </summary>
    internal ReactiveProperty<int> StateVersion { get; } = new(0);

    // ── Loaded state ──
    public ModelSelection? Models { get; private set; }
    public List<(string Name, string DisplayName, ProviderEntry Entry)> Providers { get; } = [];

    // ── Assignment flow ──
    public string? SelectedRole { get; set; }
    public string? SelectedProvider { get; set; }
    public string? SelectedModelId { get; set; }
    public List<DiscoveredModel> DiscoveredModels { get; } = [];
    public bool ManualModelEntry { get; set; }

    /// <summary>
    /// Completes when the provider probe finishes. Used for testing.
    /// </summary>
    internal Task? ProbeCompletion { get; private set; }

    public ModelManagerViewModel(NetclawPaths paths, IProviderProbe probe,
        ProviderDescriptorRegistry? registry = null, EmbeddedConfigHostMarker? embeddedHost = null)
    {
        _paths = paths;
        _probe = probe;
        _registry = registry;
        IsEmbeddedInConfig = embeddedHost is not null;
    }

    public override void OnActivated()
    {
        base.OnActivated();
        Refresh();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleGlobalKey)
            .DisposeWith(Subscriptions);
    }

    public void Refresh()
    {
        if (!Model.ModelCommand.TryLoadModelSelection(_paths, out var models))
        {
            Models = null;
            StatusMessage.Value = "Model configuration is invalid. Run `netclaw doctor` for details.";
        }
        else
        {
            Models = models;
        }
        Providers.Clear();
        var loaded = Provider.ProviderCommand.LoadProviders(_paths);
        foreach (var (name, entry) in loaded.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = _registry is not null && _registry.TryGet(entry.Type, out var descriptor)
                ? descriptor.DisplayName
                : entry.Type;
            Providers.Add((name, displayName, entry));
        }
    }

    /// <summary>
    /// Start assigning a model to the given role.
    /// </summary>
    public void StartAssignment(string role)
    {
        SelectedRole = role;
        SelectedProvider = null;
        SelectedModelId = null;
        ManualModelEntry = false;
        DiscoveredModels.Clear();

        if (Providers.Count == 0)
        {
            StatusMessage.Value = "No providers configured. Run `netclaw provider add` first.";
            RequestRedraw();
            return;
        }

        if (Providers.Count == 1)
        {
            // Auto-select the only provider
            SelectProvider(Providers[0].Name);
            return;
        }

        CurrentState.Value = ModelManagerState.SelectProvider;
        NotifyStateChanged();
    }

    /// <summary>
    /// Select a provider and start model discovery.
    /// </summary>
    public void SelectProvider(string providerName)
    {
        SelectedProvider = providerName;
        CurrentState.Value = ModelManagerState.DiscoverModels;
        NotifyStateChanged();
        StartProbe();
    }

    /// <summary>
    /// Select a discovered model and move to confirmation.
    /// </summary>
    public void SelectModel(string modelId)
    {
        SelectedModelId = modelId;
        CurrentState.Value = ModelManagerState.ConfirmAssignment;
        NotifyStateChanged();
    }

    /// <summary>
    /// Write the model assignment to config.
    /// </summary>
    public void ConfirmAssignment()
    {
        if (SelectedRole is null || SelectedProvider is null || SelectedModelId is null)
            return;

        var roleKey = SelectedRole switch
        {
            "Main" => "Main",
            "Fallback" => "Fallback",
            "Compaction" => "Compaction",
            _ => SelectedRole
        };

        var discoveredModel = ManualModelEntry
            ? null
            : DiscoveredModels.FirstOrDefault(model =>
                string.Equals(model.ModelId.Value, SelectedModelId, StringComparison.OrdinalIgnoreCase));
        var provenance = discoveredModel is null
            ? ModelDiscoverySource.Manual
            : string.IsNullOrWhiteSpace(ProbeResult.Value?.ErrorMessage)
                ? ModelDiscoverySource.Live
                : ModelDiscoverySource.Defaults;

        var (config, _) = ConfigFileHelper.LoadConfigFiles(_paths);
        var modelsSection = ConfigFileHelper.GetOrCreateSection(config, "Models");

        // Non-destructive: re-assigning the same model preserves an existing context-window
        // clamp and modality overrides, none of which the picker can supply (#1127, #1610). The
        // picker has no manual-override inputs, so it passes no explicit context window and Unset
        // modality intent — the probe result seeds a first-time set only; existing values win.
        ModelEntryWriter.WriteRole(
            modelsSection,
            roleKey,
            SelectedProvider,
            SelectedModelId,
            provenance,
            ValueOverride<int>.Unset,
            ValueOverride<ModelModality>.Unset,
            ValueOverride<ModelModality>.Unset,
            discoveredModel);
        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);

        Refresh();
        StatusMessage.Value = $"Set {SelectedRole} to {SelectedProvider}/{SelectedModelId}. Restart daemon for changes to take effect.";
        ClearAssignmentState();
        CurrentState.Value = ModelManagerState.RoleOverview;
        NotifyStateChanged();
    }

    /// <summary>
    /// Clear an optional role (Fallback or Compaction).
    /// </summary>
    public void ClearRole(string role)
    {
        if (role is "Main")
        {
            StatusMessage.Value = "Cannot clear the main model role.";
            RequestRedraw();
            return;
        }

        var roleKey = role switch
        {
            "Fallback" => "Fallback",
            "Compaction" => "Compaction",
            _ => role
        };

        var (config, _) = ConfigFileHelper.LoadConfigFiles(_paths);
        var modelsSection = ConfigFileHelper.GetSectionOrNull(config, "Models");
        if (modelsSection is not null && ModelEntryWriter.ClearRole(modelsSection, roleKey))
        {
            ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
            Refresh();
            StatusMessage.Value = $"Cleared {role} role. Restart daemon for changes to take effect.";
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Start model discovery for a specific provider without assigning.
    /// </summary>
    public void StartDiscovery(string providerName)
    {
        SelectedProvider = providerName;
        SelectedRole = null;
        CurrentState.Value = ModelManagerState.DiscoverModels;
        NotifyStateChanged();
        StartProbe();
    }

    public void GoBack()
    {
        switch (CurrentState.Value)
        {
            case ModelManagerState.SelectProvider:
            case ModelManagerState.ConfirmAssignment:
                ClearAssignmentState();
                CurrentState.Value = ModelManagerState.RoleOverview;
                NotifyStateChanged();
                break;
            case ModelManagerState.DiscoverModels:
                CancelProbe();
                if (Providers.Count > 1 && SelectedRole is not null)
                {
                    CurrentState.Value = ModelManagerState.SelectProvider;
                    NotifyStateChanged();
                }
                else
                {
                    ClearAssignmentState();
                    CurrentState.Value = ModelManagerState.RoleOverview;
                    NotifyStateChanged();
            }
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
                    // Standalone `netclaw model`: backing out past the root exits the app.
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
        var providerName = SelectedProvider;
        if (providerName is null)
            return;

        var provider = Providers.FirstOrDefault(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider.Entry is null)
            return;

        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        var providerType = provider.Entry.Type;
        var probeId = IdGen.ShortId();
        var stopwatch = Stopwatch.StartNew();
        Exception? probeException = null;

        ManualModelEntry = false;
        SelectedModelId = null;
        IsProbing.Value = true;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        DiscoveredModels.Clear();
        RequestRedraw();

        ProbeDiagnosticsLog.Write(
            _paths,
            "model-manager",
            providerType,
            provider.Entry.Endpoint,
            probeId,
            "start");

        _ = RunProbeTimerAsync(ct);

        var result = new ProviderProbeResult(false, "Validation failed before probe completed.", []);
        try
        {
            // Whole-probe wall-clock (covers pre-request work like OAuth token exchange);
            // ProbeTimeouts.InteractiveWallClock stays above the descriptor's per-request
            // deadline so it never truncates a legitimately slow self-hosted probe (#1292).
            result = await _probe.ProbeAsync(provider.Entry, ct)
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
                "model-manager",
                providerType,
                provider.Entry.Endpoint,
                probeId,
                result.Success ? "success" : "failure",
                result.ErrorMessage,
                stopwatch.Elapsed,
                probeException);
        }

        if (result.Success)
            DiscoveredModels.AddRange(result.Models.Take(MaxDisplayedModels));

        IsProbing.Value = false;
        ProbeResult.Value = result;
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

    // ── Helpers ──

    private void ClearAssignmentState()
    {
        CancelProbe();
        SelectedRole = null;
        SelectedProvider = null;
        SelectedModelId = null;
        ManualModelEntry = false;
        DiscoveredModels.Clear();
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
