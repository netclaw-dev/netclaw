using Netclaw.Configuration;
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
    private StepViewCallbacks? _callbacks;
    private FeatureSelectionStepViewModel? _vm;

    public string StepId => "feature-selection";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        _callbacks = callbacks;
        _vm = (FeatureSelectionStepViewModel)stepVm;

        var featureCount = FeatureSelectionStepViewModel.FeatureNames.Length;
        if (_cursorIndex >= featureCount) _cursorIndex = featureCount - 1;
        if (_cursorIndex < 0) _cursorIndex = 0;

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Select which features to enable for this deployment:").WithForeground(Color.White))
            .WithSpacing(1);

        for (var i = 0; i < featureCount; i++)
        {
            var isFocused = i == _cursorIndex;
            var isEnabled = _vm.IsFeatureEnabled(i);
            var prefix = isFocused ? " ▶ " : "   ";
            var checkbox = isEnabled ? "[x]" : "[ ]";
            var line = $"{prefix}{checkbox} {FeatureSelectionStepViewModel.FeatureNames[i]} — {FeatureSelectionStepViewModel.FeatureDescriptions[i]}";

            var node = new TextNode(line);
            node = isFocused
                ? node.WithForeground(Color.Cyan).Bold()
                : node.WithForeground(Color.White);
            layout = layout.WithChild(node);
        }

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

        var keyInfo = key.KeyInfo;
        var featureCount = FeatureSelectionStepViewModel.FeatureNames.Length;

        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                if (_cursorIndex > 0) _cursorIndex--;
                break;

            case ConsoleKey.DownArrow:
                if (_cursorIndex < featureCount - 1) _cursorIndex++;
                break;

            case ConsoleKey.Spacebar:
                _vm.ToggleFeature(_cursorIndex);
                break;

            case ConsoleKey.Enter:
                _callbacks?.AdvanceStep();
                return true;

            default:
                return false;
        }

        _callbacks?.InvalidateAndRedraw();
        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        // No text inputs in this step
    }

    public void ClearFocusState()
    {
        _cursorIndex = 0;
    }
}
