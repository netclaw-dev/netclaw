// -----------------------------------------------------------------------
// <copyright file="ChannelPickerStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Discord;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Unified channel picker that replaces the separate Slack/Discord wizard steps.
/// Operates as a state machine: picker mode (checklist) or sub-flow mode (delegating
/// to a child adapter's configuration sub-steps).
/// </summary>
public sealed class ChannelPickerStepViewModel : IWizardStepViewModel
{
    internal sealed record ChannelAdapterEntry(
        ChannelType Type,
        string DisplayName,
        IWizardStepViewModel Vm,
        IWizardStepView View);

    private enum Mode { Picker, SubFlow }

    private Mode _mode = Mode.Picker;
    private ChannelAdapterEntry? _activeAdapter;
    private WizardContext? _context;

    private readonly List<ChannelAdapterEntry> _adapters;
    private readonly Dictionary<ChannelType, bool> _enabled = [];
    private readonly Dictionary<ChannelType, string> _summaries = [];
    private readonly HashSet<ChannelType> _knownAdapters = [];

    public ChannelPickerStepViewModel(ISlackProbe slackProbe, IDiscordProbe discordProbe)
    {
        var slackVm = new SlackStepViewModel(slackProbe) { SkipEnableSubStep = true };
        var discordVm = new DiscordStepViewModel(discordProbe) { SkipEnableSubStep = true };
        var mattermostVm = new MattermostStepViewModel { SkipEnableSubStep = true };

        _adapters =
        [
            new ChannelAdapterEntry(ChannelType.Slack, "Slack", slackVm, new SlackStepView()),
            new ChannelAdapterEntry(ChannelType.Discord, "Discord", discordVm, new DiscordStepView()),
            new ChannelAdapterEntry(ChannelType.Mattermost, "Mattermost", mattermostVm, new MattermostStepView())
        ];

        foreach (var adapter in _adapters)
            _enabled[adapter.Type] = false;
    }

    public string StepId => WizardStepIds.ChannelPicker;
    public string DisplayTitle => "Communication Channels";

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    // ── Public API for the view ──

    internal bool IsInPickerMode => _mode == Mode.Picker;
    internal bool IsInSubFlow => _mode == Mode.SubFlow;
    internal IReadOnlyList<ChannelAdapterEntry> Adapters => _adapters;
    private int _cursorIndex;
    internal int CursorIndex
    {
        get => _cursorIndex;
        set => _cursorIndex = Math.Clamp(value, 0, Math.Max(_adapters.Count - 1, 0));
    }
    internal IWizardStepViewModel? ActiveAdapterVm => _activeAdapter?.Vm;
    internal IWizardStepView? ActiveAdapterView => _activeAdapter?.View;
    internal ChannelType? ActiveAdapterType => _activeAdapter?.Type;
    internal ChannelType SelectedAdapterType => _adapters[CursorIndex].Type;
    internal string SelectedAdapterDisplayName => _adapters[CursorIndex].DisplayName;

    internal string DoneActionText { get; set; } = "continue to next step";
    internal string DoneKeyActionLabel { get; set; } = "Done";
    internal ConsoleKey DoneKey { get; set; } = ConsoleKey.D;
    internal bool ShowDoneAction { get; set; } = true;
    internal string DoneKeyLabel => DoneKey switch
    {
        ConsoleKey.D => "d",
        ConsoleKey.S => "s",
        _ => DoneKey.ToString()
    };

    internal bool PreserveDisabledAdapterDrafts { get; set; }

    internal bool IsAdapterEnabled(int index) =>
        index >= 0 && index < _adapters.Count && _enabled[_adapters[index].Type];

    internal bool IsAdapterEnabled(ChannelType type) =>
        _enabled.TryGetValue(type, out var enabled) && enabled;

    internal bool IsAdapterKnown(ChannelType type) => _knownAdapters.Contains(type);

    internal TAdapter GetAdapterViewModel<TAdapter>(ChannelType type)
        where TAdapter : class, IWizardStepViewModel
        => _adapters.Single(a => a.Type == type).Vm as TAdapter
           ?? throw new InvalidOperationException($"Channel adapter '{type}' is not a {typeof(TAdapter).Name}.");

    internal void LoadAdapterState(
        ChannelType type,
        bool enabled,
        string? summary,
        Action<IWizardStepViewModel> configure,
        bool isKnown = false)
    {
        var adapter = _adapters.Single(a => a.Type == type);
        _enabled[type] = enabled;
        SetChildEnabled(adapter, enabled);
        configure(adapter.Vm);

        if (isKnown)
            _knownAdapters.Add(type);
        else
            _knownAdapters.Remove(type);

        if (summary is null)
            _summaries.Remove(type);
        else
            _summaries[type] = summary;
    }

    internal void ResetAdapterState(ChannelType type)
    {
        var adapter = _adapters.Single(a => a.Type == type);
        _enabled[type] = false;
        _knownAdapters.Remove(type);
        _summaries.Remove(type);
        ResetChildConfig(adapter);
    }

    internal string? GetAdapterSummary(int index) =>
        index >= 0 && index < _adapters.Count &&
        _summaries.TryGetValue(_adapters[index].Type, out var summary)
            ? summary
            : null;

    internal void SetAdapterSummary(ChannelType type, string? summary)
    {
        if (summary is null)
            _summaries.Remove(type);
        else
            _summaries[type] = summary;
    }

    internal bool AnyAdapterConfigured => _summaries.Count > 0;

    internal void ToggleAdapter(int index)
    {
        if (index < 0 || index >= _adapters.Count) return;
        var adapter = _adapters[index];

        if (_enabled[adapter.Type])
        {
            // Config-editor toggles disable without throwing away dormant setup.
            _enabled[adapter.Type] = false;
            SetChildEnabled(adapter, false);
            if (PreserveDisabledAdapterDrafts && _knownAdapters.Contains(adapter.Type))
            {
                _summaries[adapter.Type] = "disabled, saved setup";
            }
            else
            {
                _summaries.Remove(adapter.Type);
                ResetChildConfig(adapter);
            }
        }
        else
        {
            _enabled[adapter.Type] = true;
            SetChildEnabled(adapter, true);
            if (PreserveDisabledAdapterDrafts && _knownAdapters.Contains(adapter.Type))
                _summaries[adapter.Type] = ComputeSummary(adapter);
            else
                EnterSubFlow(adapter);
        }
    }

    internal void EditAdapter(int index)
    {
        if (index < 0 || index >= _adapters.Count) return;
        var adapter = _adapters[index];
        if (!_enabled[adapter.Type]) return;
        EnterSubFlow(adapter);
    }

    // ── IWizardStepViewModel ──

    public string GetHelpText()
    {
        if (_mode == Mode.SubFlow && _activeAdapter is not null)
            return _activeAdapter.Vm.GetHelpText();

        return ShowDoneAction
            ? $"  Select which communication channels to connect. Press [{DoneKeyLabel}] when done."
            : "  Select which communication channels to connect. Completed actions save automatically.";
    }

    public bool TryAdvance()
    {
        if (_mode == Mode.SubFlow && _activeAdapter is not null)
        {
            if (_activeAdapter.Vm.TryAdvance())
                return true;

            // Sub-flow complete — return to picker with summary
            CompleteSubFlow();
            return true;
        }

        // Picker mode: TryAdvance means "done with this step"
        return false;
    }

    public bool TryGoBack()
    {
        if (_mode == Mode.SubFlow && _activeAdapter is not null)
        {
            if (_activeAdapter.Vm.TryGoBack())
                return true;

            // At first sub-step of adapter — cancel and return to picker
            CancelSubFlow();
            return true;
        }

        // Picker mode: let orchestrator go to previous step
        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;
        _mode = Mode.Picker;
        _activeAdapter = null;

        if (direction == NavigationDirection.Forward)
            CursorIndex = 0;
    }

    public void OnLeave()
    {
        if (_context is null) return;

        var anyEnabled = false;
        foreach (var adapter in _adapters)
        {
            if (_enabled[adapter.Type])
            {
                adapter.Vm.OnLeave();
                anyEnabled = true;
            }
            else
            {
                _context.ChannelEntries.Remove(adapter.Type);
            }
        }

        // Additive: set the flag if any adapter is enabled, but don't clear it
        // (preserves the pattern used by the individual channel steps).
        _context.AnyChatServicesEnabled = _context.AnyChatServicesEnabled || anyEnabled;
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        foreach (var adapter in _adapters)
            adapter.Vm.ContributeConfig(builder);
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        foreach (var adapter in _adapters)
            adapter.Vm.ContributeSecrets(builder);
    }

    public async Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        foreach (var adapter in _adapters)
            await adapter.Vm.ContributeHealthChecksAsync(runner, ct);
    }

    public void Dispose()
    {
        foreach (var adapter in _adapters)
            adapter.Vm.Dispose();
    }

    // ── Private helpers ──

    private void EnterSubFlow(ChannelAdapterEntry adapter)
    {
        _mode = Mode.SubFlow;
        _activeAdapter = adapter;
        adapter.Vm.OnEnter(_context!, NavigationDirection.Forward);
    }

    private void CompleteSubFlow()
    {
        var adapter = _activeAdapter!;
        _summaries[adapter.Type] = ComputeSummary(adapter);
        _knownAdapters.Add(adapter.Type);
        _mode = Mode.Picker;
        _activeAdapter = null;
    }

    private void CancelSubFlow()
    {
        var adapter = _activeAdapter!;

        // If this was a fresh toggle-on (no prior summary), revert to unchecked
        if (!_summaries.ContainsKey(adapter.Type))
        {
            _enabled[adapter.Type] = false;
            ResetChildConfig(adapter);
        }

        _mode = Mode.Picker;
        _activeAdapter = null;
    }

    private static string ComputeSummary(ChannelAdapterEntry adapter)
    {
        var adapterVm = (IChannelAdapterViewModel)adapter.Vm;
        var count = adapterVm.ConfiguredChannelCount;
        if (adapterVm.AllowDirectMessages) count++;

        return count switch
        {
            0 => "configured",
            1 => "1 channel configured",
            _ => $"{count} channels configured"
        };
    }

    private static void SetChildEnabled(ChannelAdapterEntry adapter, bool enabled)
    {
        ((IChannelAdapterViewModel)adapter.Vm).AdapterEnabled = enabled;
    }

    private static void ResetChildConfig(ChannelAdapterEntry adapter)
    {
        ((IChannelAdapterViewModel)adapter.Vm).ResetConfig();
    }
}
