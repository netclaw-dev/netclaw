using R3;
using Termina.Input;
using Termina.Layout;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Callbacks provided by the wizard page to step views for triggering
/// layout rebuilds, help text updates, and step advancement.
/// </summary>
public sealed class StepViewCallbacks
{
    /// <summary>Step-scoped subscriptions. Cleared when the step content is rebuilt.</summary>
    public required CompositeDisposable Subscriptions { get; init; }

    /// <summary>Invalidate and rebuild the step content layout.</summary>
    public required Action InvalidateContent { get; init; }

    /// <summary>Invalidate and rebuild the help text.</summary>
    public required Action InvalidateHelp { get; init; }

    /// <summary>
    /// Signal that the step is complete and the wizard should advance.
    /// Maps to <c>orchestrator.GoNext()</c>.
    /// </summary>
    public required Action AdvanceStep { get; init; }

    /// <summary>Request a terminal redraw.</summary>
    public required Action RequestRedraw { get; init; }
}

/// <summary>
/// Builds the Termina layout for a wizard step. Paired with an <see cref="IWizardStepViewModel"/>.
/// Each step view owns its own input components, focus state, and subscriptions.
/// </summary>
public interface IWizardStepView
{
    /// <summary>The step ID this view is paired with.</summary>
    string StepId { get; }

    /// <summary>
    /// Build the layout for the current sub-step state and wire all reactive
    /// subscriptions (selection confirmations, input submissions, etc.).
    /// </summary>
    ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks);

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
}
