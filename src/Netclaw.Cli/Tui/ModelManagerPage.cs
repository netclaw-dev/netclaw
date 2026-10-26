// -----------------------------------------------------------------------
// <copyright file="ModelManagerPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the <c>netclaw model</c> interactive TUI.
/// Provides viewing model role assignments, discovering models, and assigning them.
/// </summary>
public sealed class ModelManagerPage : ReactivePage<ModelManagerViewModel>
{
    private const int MaxOutWidth = 13;
    private const int ThinkingWidth = 8;

    private readonly IAnsiTerminal _terminal;
    private SelectionListNode<string>? _roleList;
    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _modelList;
    private TextInputNode? _manualModelInput;
    private SelectionListNode<string>? _confirmList;
    private TextInputNode? _limitInput;
    private SelectionListNode<string>? _levelList;

    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;
    private KeyedDynamicLayoutNode<(ModelManagerContent Content, int Revision)>? _contentNode;
    private int _roleOverviewHighlight;
    private readonly CompositeDisposable _stepSubs = [];

    public ModelManagerPage(IAnsiTerminal terminal)
    {
        _terminal = terminal;
    }

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return NetclawTuiChrome.BuildPageFrame("Model Manager", BuildInnerLayout());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new KeyedDynamicLayoutNode<(ModelManagerContent Content, int Revision)>(
            GetContentKey,
            _ =>
            {
                _lastFocusedList = null;
                _lastFocusedInput = null;
                _stepSubs.Clear();

                return ViewModel.CurrentState.Value switch
                {
                    ModelManagerState.Loading => BuildLoading(),
                    ModelManagerState.RoleOverview => BuildRoleOverview(),
                    ModelManagerState.SelectProvider => BuildProviderSelection(),
                    ModelManagerState.DiscoverModels => BuildDiscoverModels(),
                    ModelManagerState.ConfirmAssignment => BuildConfirmAssignment(),
                    ModelManagerState.Properties => BuildProperties(),
                    _ => Layouts.Empty()
                };
            },
            KeyedDynamicCachePolicy.EvictOnKeyChange);

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _contentNode;
    }

    private (ModelManagerContent Content, int Revision) GetContentKey()
    {
        // RoleOverview carries the revision in its key. A clear edits the table in place:
        // the state stays RoleOverview, so a content-only key never rebuilds it and the
        // cleared role stays on screen. Every other view keys on its own state or sub-state.
        if (ViewModel.CurrentState.Value != ModelManagerState.DiscoverModels)
        {
            var content = (ModelManagerContent)ViewModel.CurrentState.Value;
            if (content == ModelManagerContent.Properties && ViewModel.Properties is null)
                content = ModelManagerContent.PropertiesProbing;
            var revision = content == ModelManagerContent.RoleOverview ? ViewModel.StateVersion.Value : 0;
            return (content, revision);
        }

        if (ViewModel.IsProbing.Value)
            return (ModelManagerContent.DiscoverProbing, 0);
        if (ViewModel.ProbeResult.Value is { Success: false })
            return (ModelManagerContent.DiscoverFailed, 0);
        if (ViewModel.ManualModelEntry)
            return (ModelManagerContent.DiscoverManualEntry, 0);
        if (ViewModel.ProbeResult.Value is not null && ViewModel.DiscoveredModels.Count == 0)
            return (ModelManagerContent.DiscoverEmpty, 0);

        return (ModelManagerContent.DiscoverList, 0);
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Green))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return ViewModel.CurrentState
            .Select(state =>
            {
                var text = state switch
                {
                    ModelManagerState.RoleOverview =>
                        // Embedded in `netclaw config`, Esc backs out to the dashboard; standalone Ctrl+Q quits.
                        ViewModel.IsEmbeddedInConfig
                            ? " [\u2191/\u2193] Navigate  [Enter] Assign  [D] Discover  [P] Properties  [C] Clear  [Esc] Back  [Ctrl+Q] Quit"
                            : " [\u2191/\u2193] Navigate  [Enter] Assign  [D] Discover  [P] Properties  [C] Clear  [Ctrl+Q] Quit",
                    ModelManagerState.ConfirmAssignment =>
                        " [Enter] Confirm  [Esc] Cancel",
                    ModelManagerState.Properties =>
                        " [Tab] Switch field  [\u2191/\u2193] Level  [Enter] Save  [Esc] Cancel  [Ctrl+Q] Quit",
                    _ =>
                        " [\u2191/\u2193] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit"
                };
                return (ILayoutNode)NetclawTuiChrome.BuildKeyHintLine(text);
            })
            .AsLayout()
            .Height(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Content views
    // ═══════════════════════════════════════════════════════════════════

    private static ILayoutNode BuildLoading()
    {
        return new TextNode("  Loading model configuration...")
            .WithForeground(Color.BrightBlack);
    }

    private ILayoutNode BuildRoleOverview()
    {
        var items = new[] { "Main", "Fallback", "Compaction" };
        var models = items.ToDictionary(
            static role => role,
            role => role switch
            {
                "Main" => ViewModel.Models?.Main,
                "Fallback" => ViewModel.Models?.Fallback,
                "Compaction" => ViewModel.Models?.Compaction,
                _ => null
            });

        // Size the Model ID and Provider (capped) columns to their longest values so
        // Status stays aligned, leaving room for the Role (12), Max out, Thinking, and
        // Status (12) columns and the list prefix and panel border. The model ID gets the
        // room first, because a cut model ID cannot be told apart from another one.
        var available = _terminal.Width - 12 - 12 - 12 - (MaxOutWidth + 1) - (ThinkingWidth + 1);
        var providerLabels = models.Values.Select(ProviderLabel).ToList();
        var modelWidth = NetclawTuiChrome.FitColumnWidth(
            models.Values.Select(static m => m?.ModelId ?? "\u2014"), "Model ID", available - 20);
        var providerWidth = NetclawTuiChrome.FitColumnWidth(providerLabels, "Provider",
            Math.Min(NetclawTuiChrome.MaxProviderColumnWidth, available - modelWidth));

        // A clear rebuilds this subtree while the user stays on the role table. Keep the cursor
        // on the same row instead of snapping back to Main. The previous list is still alive
        // here: the parent retires it only after this factory returns.
        if (_roleList?.HighlightedItem is { } prior)
        {
            // IndexOf cannot miss today: the items array is a fixed three-role list. Guard
            // anyway so a future list that drops a name cannot carry -1 into the new index.
            var priorIndex = Array.IndexOf(items, prior.Value);
            _roleOverviewHighlight = priorIndex >= 0 ? priorIndex : 0;
        }

        _roleList = Layouts.SelectionList(items, role => FormatRoleItem(role, models[role], providerWidth, modelWidth))
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan)
            .WithHighlightedIndex(_roleOverviewHighlight);

        _roleList.OnFocused();
        _lastFocusedList = _roleList;

        _roleList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.StartAssignment(selected[0]);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Model Role Assignments").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"   {"Role",-12} {NetclawTuiChrome.FitColumn("Provider", providerWidth)} {NetclawTuiChrome.FitColumn("Model ID", modelWidth)} {"Max out",-MaxOutWidth} {"Thinking",-ThinkingWidth} Status")
                .WithForeground(Color.Gray))
            .WithChild(_roleList)
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode("  [Enter] Assign model  [D] Discover models  [P] Model properties  [C] Clear optional role")
                .WithForeground(Color.Gray));
    }

    private string ProviderLabel(ModelReference? model)
    {
        if (model is null)
            return "(not set)";

        var match = ViewModel.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, model.Provider, StringComparison.OrdinalIgnoreCase));
        return match.Name is not null ? $"{match.Name} ({match.DisplayName})" : model.Provider;
    }

    private string FormatRoleItem(string role, ModelReference? model, int providerWidth, int modelWidth)
    {
        if (model is null)
            return $"{role,-12} {NetclawTuiChrome.FitColumn(ProviderLabel(null), providerWidth)} {NetclawTuiChrome.FitColumn("\u2014", modelWidth)} {"\u2014",-MaxOutWidth} {"\u2014",-ThinkingWidth} \u2014";

        var maxOut = model.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     ?? $"default {ModelReference.DefaultMaxOutputTokens}";
        var thinking = model.Thinking is { } level ? ThinkingDialectMap.Format(level) : ModelPropertiesLevels.Default;
        return $"{role,-12} {NetclawTuiChrome.FitColumn(ProviderLabel(model), providerWidth)} {NetclawTuiChrome.FitColumn(model.ModelId, modelWidth)} {maxOut,-MaxOutWidth} {thinking,-ThinkingWidth} {(model.Provenance?.ToString() ?? "unknown")}";
    }

    private ILayoutNode BuildProviderSelection()
    {
        var items = ViewModel.Providers
            .Select(p => $"{p.Name} ({p.DisplayName})")
            .ToList();

        _providerList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _providerList.OnFocused();
        _lastFocusedList = _providerList;

        _providerList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var idx = items.IndexOf(selected[0]);
                    if (idx >= 0 && idx < ViewModel.Providers.Count)
                        ViewModel.SelectProvider(ViewModel.Providers[idx].Name);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Select provider for {ViewModel.SelectedRole ?? "role"}:")
                .WithForeground(Color.White))
            .WithChild(_providerList.WithFillHeight());
    }

    private ILayoutNode BuildDiscoverModels()
    {
        if (ViewModel.IsProbing.Value)
        {
            return Layouts.Vertical()
                .WithChild(SpinnerViews.WithElapsed(
                    $"Discovering models from '{ViewModel.SelectedProvider}'...", Color.Yellow,
                    ViewModel.ProbeElapsedSeconds));
        }

        var result = ViewModel.ProbeResult.Value;
        if (result is { Success: false })
        {
            return Layouts.Vertical()
                .WithChild(new TextNode($"  \u2718 Discovery failed: {result.ErrorMessage}")
                    .WithForeground(Color.Red))
                .WithChild(new TextNode("  Press [Enter] to retry, [Esc] to go back.")
                    .WithForeground(Color.Gray));
        }

        if (ViewModel.DiscoveredModels.Count == 0 && result is not null)
        {
            return Layouts.Vertical()
                .WithChild(new TextNode("  No models found on this provider.")
                    .WithForeground(Color.Gray))
                .WithChild(new TextNode("  Press [Esc] to go back.")
                    .WithForeground(Color.Gray));
        }

        // Check if manual entry is active
        if (ViewModel.ManualModelEntry)
        {
            _manualModelInput = new TextInputNode()
                .WithPlaceholder("Enter model ID...");
            _manualModelInput.OnFocused();
            _lastFocusedInput = _manualModelInput;

            _manualModelInput.Submitted
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Subscribe(text =>
                {
                    if (ViewModel.SelectedRole is not null)
                        ViewModel.SelectModel(text);
                })
                .DisposeWith(_stepSubs);

            return Layouts.Vertical()
                .WithChild(new TextNode($"  Models from '{ViewModel.SelectedProvider}' ({ViewModel.DiscoveredModels.Count} found):")
                    .WithForeground(Color.White))
                .WithChild(new TextNode("").Height(1))
                .WithChild(new TextNode("  Enter model ID:").WithForeground(Color.White))
                .WithChild(NetclawTuiChrome.BuildTextInputPanel(_manualModelInput, "Model ID"));
        }

        // Build model list with manual entry option
        var items = ViewModel.DiscoveredModels
            .Select(m => m.ModelId.Value)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        items.Add("Enter model ID manually...");

        _modelList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _modelList.OnFocused();
        _lastFocusedList = _modelList;

        _modelList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0) return;

                if (selected[0] == "Enter model ID manually...")
                {
                    ViewModel.ManualModelEntry = true;
                    ViewModel.StateVersion.Value++;
                    ViewModel.RequestRedraw();
                }
                else if (ViewModel.SelectedRole is not null)
                {
                    ViewModel.SelectModel(selected[0]);
                }
                else
                {
                    ViewModel.StatusMessage.Value = $"Model: {selected[0]}";
                    ViewModel.RequestRedraw();
                }
            })
            .DisposeWith(_stepSubs);

        var discoverProviderLabel = ViewModel.SelectedProvider ?? "";
        var discoverMatch = ViewModel.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, ViewModel.SelectedProvider, StringComparison.OrdinalIgnoreCase));
        if (discoverMatch.Name is not null)
            discoverProviderLabel = $"{discoverMatch.Name} ({discoverMatch.DisplayName})";

        var title = ViewModel.SelectedRole is not null
            ? $"  Select model for {ViewModel.SelectedRole} (from '{discoverProviderLabel}'):"
            : $"  Models from '{discoverProviderLabel}' ({ViewModel.DiscoveredModels.Count} found):";

        return Layouts.Vertical()
            .WithChild(new TextNode(title).WithForeground(Color.White))
            .WithChild(_modelList.WithFillHeight());
    }

    private ILayoutNode BuildConfirmAssignment()
    {
        var items = new List<string> { "Yes, assign", "No, cancel" };
        _confirmList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Green);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;

        _confirmList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0 && selected[0].StartsWith("Yes", StringComparison.Ordinal))
                    ViewModel.ConfirmAssignment();
                else
                    ViewModel.GoBack();
            })
            .DisposeWith(_stepSubs);

        var providerLabel = ViewModel.SelectedProvider ?? "";
        var providerMatch = ViewModel.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, ViewModel.SelectedProvider, StringComparison.OrdinalIgnoreCase));
        if (providerMatch.Name is not null)
            providerLabel = $"{providerMatch.Name} ({providerMatch.DisplayName})";

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Assign {ViewModel.SelectedRole} model?")
                .WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"    Provider: {providerLabel}")
                .WithForeground(Color.White))
            .WithChild(new TextNode($"    Model:    {ViewModel.SelectedModelId}")
                .WithForeground(Color.White))
            .WithChild(new TextNode("").Height(1))
            .WithChild(_confirmList);
    }

    private ILayoutNode BuildProperties()
    {
        _limitInput = null;
        _levelList = null;
        if (ViewModel.Properties is not { } panel)
        {
            return Layouts.Vertical()
                .WithChild(SpinnerViews.Labeled("Probing the provider for the model capability...", Color.Yellow));
        }

        _limitInput = new TextInputNode().WithPlaceholder($"default {ModelReference.DefaultMaxOutputTokens}");
        NetclawTuiChrome.SeedTextInput(_limitInput, panel.Model.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _limitInput.OnFocused();
        _lastFocusedInput = _limitInput;

        var layout = Layouts.Vertical()
            .WithChild(new TextNode($"  Model properties: {panel.DefinitionName}").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"    Role: {panel.Role}   Provider: {panel.Model.Provider}   Model: {panel.Model.ModelId}")
                .WithForeground(Color.White));
        if (panel.AlsoUsedBy.Count > 0)
        {
            layout = layout.WithChild(new TextNode($"    Also used by: {string.Join(", ", panel.AlsoUsedBy)}. A change applies to each role.")
                .WithForeground(Color.Yellow));
        }

        layout = layout
            .WithChild(new TextNode("").Height(1))
            .WithChild(Layouts.Horizontal()
                .WithChild(new TextNode("  Max output tokens: ").WithForeground(Color.White))
                .WithChild(_limitInput)
                .Height(1))
            .WithChild(new TextNode($"    Blank means the default: {ModelReference.DefaultMaxOutputTokens}, clamped per call. Thinking tokens count inside the limit.")
                .WithForeground(Color.Gray))
            .WithChild(new TextNode("").Height(1));

        if (panel.ProbeNote is { } note)
            layout = layout.WithChild(new TextNode($"  {note}").WithForeground(Color.Yellow));

        if (panel.DialectProblem is { } problem)
        {
            // Only the output field is editable: no level can be sent without a dialect.
            return layout.WithChild(new TextNode($"  Thinking level: {problem}").WithForeground(Color.Yellow));
        }

        var rows = ModelPropertiesLevels.Rows;
        var current = panel.Model.Thinking is { } level ? ThinkingDialectMap.Format(level) : ModelPropertiesLevels.Default;
        _levelList = Layouts.SelectionList(rows, row => FormatLevelRow(panel, row))
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan)
            .WithHighlightedIndex(Math.Max(0, rows.ToList().IndexOf(current)));

        return layout
            .WithChild(new TextNode("  Thinking level:").WithForeground(Color.White))
            .WithChild(_levelList);
    }

    // A level that is not offered stays in the list, greyed by its reason, so the operator sees why.
    private static string FormatLevelRow(ModelPropertiesState panel, string row)
    {
        if (row == ModelPropertiesLevels.Default)
            return "default (the model's own default)";
        var level = Enum.Parse<ThinkingLevel>(row, ignoreCase: true);
        return panel.Offered.Unavailable.TryGetValue(level, out var reason)
            ? $"{row,-7} \u2014 not offered: {reason}"
            : row;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Input handling
    // ═══════════════════════════════════════════════════════════════════

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        var state = ViewModel.CurrentState.Value;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.GoBack();
            return;
        }

        if (state == ModelManagerState.Properties)
        {
            HandlePropertiesKey(keyInfo);
            return;
        }

        // Role overview shortcuts
        if (state == ModelManagerState.RoleOverview)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.D:
                    if (ViewModel.Providers.Count == 1)
                        ViewModel.StartDiscovery(ViewModel.Providers[0].Name);
                    else if (ViewModel.Providers.Count > 1)
                    {
                        ViewModel.SelectedRole = null;
                        ViewModel.CurrentState.Value = ModelManagerState.SelectProvider;
                        ViewModel.StateVersion.Value++;
                        ViewModel.RequestRedraw();
                    }
                    return;
                case ConsoleKey.P:
                    if (_roleList?.HighlightedItem is { } propertiesRole)
                        ViewModel.OpenProperties(propertiesRole.Value);
                    return;
                case ConsoleKey.C:
                    // Read the live highlight, not SelectedItems. In single mode an arrow key
                    // moves the highlight without selecting, so SelectedItems stays empty until
                    // Enter or Space. The old read meant C did nothing after plain arrow
                    // navigation. Provider manager hit the same bug class in #1721.
                    if (_roleList?.HighlightedItem is { } highlighted)
                    {
                        ViewModel.ClearRole(highlighted.Value);
                    }
                    return;
            }
        }

        // Retry on discover failure
        if (state == ModelManagerState.DiscoverModels && keyInfo.Key == ConsoleKey.Enter)
        {
            var result = ViewModel.ProbeResult.Value;
            if (result is { Success: false })
            {
                ViewModel.StartProbe();
                return;
            }
        }

        RouteInputToActiveComponent(keyInfo);
    }

    private enum ModelManagerContent
    {
        RoleOverview = ModelManagerState.RoleOverview,
        SelectProvider = ModelManagerState.SelectProvider,
        ConfirmAssignment = ModelManagerState.ConfirmAssignment,
        Loading = ModelManagerState.Loading,
        Properties = ModelManagerState.Properties,
        // Sub-states start after the last state, so no two contents share a key.
        DiscoverProbing = 100,
        DiscoverFailed,
        DiscoverEmpty,
        DiscoverManualEntry,
        DiscoverList,
        PropertiesProbing,
    }

    private void HandlePropertiesKey(ConsoleKeyInfo keyInfo)
    {
        if (ViewModel.Properties is null || _limitInput is null)
            return;

        switch (keyInfo.Key)
        {
            case ConsoleKey.Enter:
                // Enter saves from either field. The picker highlight starts on the stored level, so
                // a save from the limit field keeps the level unless the operator moved the picker.
                ViewModel.SaveProperties(_limitInput.Text, _levelList?.HighlightedItem?.Value);
                return;
            case ConsoleKey.Tab when _levelList is not null:
                if (_lastFocusedInput is not null)
                {
                    _limitInput.OnBlurred();
                    _lastFocusedInput = null;
                    _levelList.OnFocused();
                    _lastFocusedList = _levelList;
                }
                else
                {
                    _levelList.OnBlurred();
                    _lastFocusedList = null;
                    _limitInput.OnFocused();
                    _lastFocusedInput = _limitInput;
                }

                ViewModel.RequestRedraw();
                return;
            default:
                RouteInputToActiveComponent(keyInfo);
                return;
        }
    }

    private void RouteInputToActiveComponent(ConsoleKeyInfo keyInfo)
    {
        if (_lastFocusedInput is not null)
        {
            _lastFocusedInput.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
            return;
        }

        if (_lastFocusedList is not null)
        {
            ((SelectionListNode<string>)_lastFocusedList).HandleInput(keyInfo);
            ViewModel.RequestRedraw();
        }
    }
}
