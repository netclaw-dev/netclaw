// -----------------------------------------------------------------------
// <copyright file="ModelManagerPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
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
    private SelectionListNode<string>? _roleList;
    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _modelList;
    private TextInputNode? _manualModelInput;
    private SelectionListNode<string>? _confirmList;

    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _stepSubs = [];

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
        _contentNode = new DynamicLayoutNode(() =>
        {
            _lastFocusedList = null;
            _lastFocusedInput = null;
            _stepSubs.Clear();

            return ViewModel.CurrentState.Value switch
            {
                ModelManagerState.RoleOverview => BuildRoleOverview(),
                ModelManagerState.SelectProvider => BuildProviderSelection(),
                ModelManagerState.DiscoverModels => BuildDiscoverModels(),
                ModelManagerState.ConfirmAssignment => BuildConfirmAssignment(),
                _ => Layouts.Empty()
            };
        });

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _contentNode;
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
                            ? " [\u2191/\u2193] Navigate  [Enter] Assign  [D] Discover  [C] Clear  [Esc] Back  [Ctrl+Q] Quit"
                            : " [\u2191/\u2193] Navigate  [Enter] Assign  [D] Discover  [C] Clear  [Ctrl+Q] Quit",
                    ModelManagerState.ConfirmAssignment =>
                        " [Enter] Confirm  [Esc] Cancel",
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

    private ILayoutNode BuildRoleOverview()
    {
        var models = ViewModel.Models;
        var items = new List<string>
        {
            FormatRoleItem("Main", models?.Main),
            FormatRoleItem("Fallback", models?.Fallback),
            FormatRoleItem("Compaction", models?.Compaction)
        };

        _roleList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _roleList.OnFocused();
        _lastFocusedList = _roleList;

        _roleList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var role = selected[0].Split(' ', 2)[0].Trim();
                    ViewModel.StartAssignment(role);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Model Role Assignments").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  {"Role",-12} {"Provider",-28} {"Model ID",-28} Status")
                .WithForeground(Color.Gray))
            .WithChild(_roleList)
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode("  [Enter] Assign model  [D] Discover models  [C] Clear optional role")
                .WithForeground(Color.Gray));
    }

    private string FormatRoleItem(string role, ModelReference? model)
    {
        if (model is null)
            return $"{role,-12} {"(not set)",-28} {"\u2014",-28} \u2014";

        var providerLabel = model.Provider;
        var match = ViewModel.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, model.Provider, StringComparison.OrdinalIgnoreCase));
        if (match.Name is not null)
            providerLabel = $"{match.Name} ({match.DisplayName})";

        return $"{role,-12} {providerLabel,-28} {model.ModelId,-28} {(model.Provenance?.ToString() ?? "unknown")}";
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
                case ConsoleKey.C:
                    if (_roleList is not null)
                    {
                        var selectedItems = _roleList.SelectedItems;
                        if (selectedItems.Count > 0)
                        {
                            var role = selectedItems[0].Split(' ', 2)[0].Trim();
                            ViewModel.ClearRole(role);
                        }
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
