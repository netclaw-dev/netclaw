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
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(20);

    private readonly NetclawPaths _paths;
    private readonly IProviderProbe _probe;
    private readonly ProviderDescriptorRegistry? _registry;
    private CancellationTokenSource? _probeCts;

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
        ProviderDescriptorRegistry? registry = null)
    {
        _paths = paths;
        _probe = probe;
        _registry = registry;
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
        Models = Model.ModelCommand.LoadModelSelection(_paths);
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

        var provenance = ManualModelEntry
            ? ModelDiscoverySource.Manual
            : ModelDiscoverySource.Live;

        var (config, _) = ConfigFileHelper.LoadConfigFiles(_paths);
        var modelsSection = ConfigFileHelper.GetOrCreateSection(config, "Models");

        var modelEntry = new Dictionary<string, object>
        {
            ["Provider"] = SelectedProvider,
            ["ModelId"] = SelectedModelId,
            ["Provenance"] = provenance.ToString()
        };

        modelsSection[roleKey] = modelEntry;
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
        if (modelsSection?.Remove(roleKey) == true)
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
            result = await _probe.ProbeAsync(provider.Entry, ct)
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
