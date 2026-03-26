using R3;
using Termina.Input;
using Termina.Layout;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Builds the Termina layout for a wizard step. Paired with an <see cref="IWizardStepViewModel"/>.
/// Each step view owns its own input components, focus state, and subscriptions.
/// </summary>
public interface IWizardStepView
{
    /// <summary>The step ID this view is paired with.</summary>
    string StepId { get; }

    /// <summary>Build the layout for the current sub-step state.</summary>
    ILayoutNode BuildContent(IWizardStepViewModel stepVm);

    /// <summary>
    /// Route a key press to the appropriate interactive component.
    /// Returns <c>true</c> if the input was consumed.
    /// </summary>
    bool HandleKeyPress(KeyPressed key);

    /// <summary>Handle bracketed paste to the active text input.</summary>
    void HandlePaste(PasteEvent paste);

    /// <summary>
    /// Reset focus references and clear step-specific subscriptions.
    /// Called before rebuilding step content on step change.
    /// </summary>
    void ClearFocusState();

    /// <summary>
    /// Wire reactive subscriptions (e.g., spinner tick, probe result).
    /// Called after content is built. The <paramref name="invalidateContent"/>
    /// callback triggers a layout rebuild; <paramref name="invalidateHelp"/>
    /// triggers a help text rebuild.
    /// </summary>
    void WireSubscriptions(CompositeDisposable subscriptions, Action invalidateContent, Action invalidateHelp);
}
