// -----------------------------------------------------------------------
// <copyright file="FeatureSelectionStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Cli.Tui.Workflow;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Feature Selection wizard step.
/// Displays a checkbox list of deployment-wide feature toggles.
/// Uses manual cursor navigation (Arrow keys), Space to toggle, Enter to confirm.
/// </summary>
public sealed class FeatureSelectionStepView : IWizardStepView
{
    private int _cursorIndex;
    private ActiveSelectionList<FeatureToggleOption>? _featureList;
    private StepViewCallbacks? _callbacks;
    private FeatureSelectionStepViewModel? _vm;

    public string StepId => WizardStepIds.FeatureSelection;

    public bool ManagesOwnFocusState => true;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        _callbacks = callbacks;
        _vm = (FeatureSelectionStepViewModel)stepVm;

        var options = FeatureToggleOption.All;
        if (_cursorIndex >= options.Count) _cursorIndex = options.Count - 1;
        if (_cursorIndex < 0) _cursorIndex = 0;

        _featureList = new ActiveSelectionList<FeatureToggleOption>(
            options,
            static option => option.Name.PadRight(12),
            option => _vm.IsFeatureEnabled(option.Index),
            static option => option.Description,
            focusedIndex: _cursorIndex,
            toggled: option => _vm.ToggleFeature(option.Index),
            changed: () =>
            {
                _cursorIndex = _featureList?.FocusedIndex ?? _cursorIndex;
                callbacks.RequestRedraw();
            });

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Select which features to enable for this deployment:").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(_featureList.AsLayout());

        layout = layout.WithSpacing(1)
            .WithChild(new TextNode("  Space to toggle, Enter to continue.")
                .WithForeground(Color.BrightBlack));

        // Add search note for Public posture
        if (_vm.CurrentPosture == DeploymentPosture.Public)
        {
            layout = layout.WithChild(
                new TextNode("  Note: enabling Search only enables the runtime. Public sessions still require explicit tool allowlisting for web_search/web_fetch.")
                    .WithForeground(Color.BrightBlack));
        }

        return layout;
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_vm is null)
            return false;

        switch (key.KeyInfo.Key)
        {
            case ConsoleKey.Enter:
                _callbacks?.AdvanceStep();
                return true;
        }

        return _featureList?.HandleInput(key.KeyInfo) ?? false;
    }

    public void HandlePaste(PasteEvent paste)
    {
        // No text inputs in this step
    }

    public void ClearFocusState()
    {
        _cursorIndex = 0;
        _featureList = null;
    }

    private sealed record FeatureToggleOption(int Index, string Name, string Description)
    {
        public static readonly IReadOnlyList<FeatureToggleOption> All =
        [
            new(0, FeatureSelectionStepViewModel.FeatureNames[0], FeatureSelectionStepViewModel.FeatureDescriptions[0]),
            new(1, FeatureSelectionStepViewModel.FeatureNames[1], FeatureSelectionStepViewModel.FeatureDescriptions[1]),
            new(2, FeatureSelectionStepViewModel.FeatureNames[2], FeatureSelectionStepViewModel.FeatureDescriptions[2]),
            new(3, FeatureSelectionStepViewModel.FeatureNames[3], FeatureSelectionStepViewModel.FeatureDescriptions[3]),
            new(4, FeatureSelectionStepViewModel.FeatureNames[4], FeatureSelectionStepViewModel.FeatureDescriptions[4]),
            new(5, FeatureSelectionStepViewModel.FeatureNames[5], FeatureSelectionStepViewModel.FeatureDescriptions[5])
        ];
    }
}
