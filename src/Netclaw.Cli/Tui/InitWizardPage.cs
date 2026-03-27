using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Providers.OAuth;
using R3;
using Termina.Clipboard;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the <c>netclaw init</c> onboarding wizard.
/// Layout: outer panel with step indicator, step-specific content, help text, key bindings.
/// Delegates step rendering to <see cref="IWizardStepView"/> instances provided by the ViewModel.
/// </summary>
public sealed class InitWizardPage : ReactivePage<InitWizardViewModel>
{
    private readonly IClipboardService? _clipboardService;

    public InitWizardPage(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
    }

    // Dynamic layout nodes — invalidation-driven (Termina 0.7.1+).
    private DynamicLayoutNode? _stepContentNode;
    private DynamicLayoutNode? _helpTextNode;

    // Step-specific subscriptions — cleared when step content is rebuilt.
    private readonly CompositeDisposable _stepSubs = new();

    protected override void OnBound()
    {
        base.OnBound();

        // Route keyboard input
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        // Route bracketed paste events
        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        // Wire content invalidation for ALL navigation (step and sub-step changes)
        ViewModel.OnStepContentChanged = () =>
        {
            _stepContentNode?.Invalidate();
            _helpTextNode?.Invalidate();
        };

        // When the orchestrator's step index changes, clear subs and invalidate layouts
        ViewModel.Orchestrator.CurrentStepIndex
            .Subscribe(_ =>
            {
                _stepSubs.Clear();
                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);

        // Provider step: auto-advance on probe success
        ViewModel.ProviderStep.ProbeResult
            .Subscribe(result =>
            {
                if (ViewModel.Orchestrator.CurrentStep is not ProviderStepViewModel { CurrentSubStep: 3 })
                    return;

                // Always invalidate to render the new state (success flash or error)
                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();

                // Auto-advance to model selection on success
                if (result is { Success: true })
                {
                    ViewModel.ProviderStep.SetSubStep(4);
                    _stepContentNode?.Invalidate();
                    _helpTextNode?.Invalidate();
                    ViewModel.RequestRedraw();
                }
            })
            .DisposeWith(Subscriptions);

        // Provider step: animate spinner on validation/OAuth sub-steps
        ViewModel.ProviderStep.SpinnerTick
            .Subscribe(_ =>
            {
                if (ViewModel.Orchestrator.CurrentStep is ProviderStepViewModel { CurrentSubStep: 3 or 5 or 6 })
                {
                    _stepContentNode?.Invalidate();
                    ViewModel.RequestRedraw();
                }
            })
            .DisposeWith(Subscriptions);

        // Provider step: OAuth flow state changes
        ViewModel.ProviderStep.OAuth.FlowState
            .Subscribe(state =>
            {
                if (ViewModel.Orchestrator.CurrentStep is not ProviderStepViewModel { CurrentSubStep: 5 or 6 })
                    return;

                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();

                // Auto-advance to validation on success
                if (state == DeviceFlowState.Succeeded)
                {
                    ViewModel.ProviderStep.SetSubStep(3);
                    ViewModel.ProviderStep.StartProbe();
                    _stepContentNode?.Invalidate();
                    _helpTextNode?.Invalidate();
                    ViewModel.RequestRedraw();
                }
            })
            .DisposeWith(Subscriptions);

        // Health check: result version changes
        ViewModel.HealthCheckStep.ResultVersion
            .Subscribe(_ =>
            {
                if (ViewModel.Orchestrator.CurrentStep is HealthCheckStepViewModel)
                    _stepContentNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Netclaw Setup")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildStepIndicator())
            .WithChild(BuildStepContent())
            .WithChild(BuildHelpText())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildStepIndicator()
    {
        return ViewModel.Orchestrator.CurrentStepIndex
            .Select(_ =>
            {
                var step = ViewModel.Orchestrator.CurrentStep;
                if (step is null) return (ILayoutNode)Layouts.Empty();

                var activeCount = ViewModel.Orchestrator.ActiveStepCount;
                var displayNum = ViewModel.Orchestrator.GetDisplayStepNumber();
                var filled = new string('\u25a0', displayNum);
                var empty = new string('\u25a1', activeCount - displayNum);
                var pct = displayNum * 100 / activeCount;
                var title = step.DisplayTitle;

                return (ILayoutNode)new TextNode(
                        $"  Step {displayNum} of {activeCount}: {title}        [{filled}{empty}] {pct}%")
                    .WithForeground(Color.White)
                    .Bold();
            })
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildStepContent()
    {
        _stepContentNode = new DynamicLayoutNode(() =>
        {
            var step = ViewModel.Orchestrator.CurrentStep;
            if (step is null) return Layouts.Empty();

            // Health check has no stateful components — safe to rebuild on every invalidation
            if (step is HealthCheckStepViewModel)
            {
                var hcView = ViewModel.StepViews["health-check"];
                return hcView.BuildContent(step, CreateCallbacks());
            }

            // Validation and OAuth sub-steps are stateless renders — just a spinner or error text.
            // Skip clearing focus/subs so the spinner can tick without disposing interactive state.
            if (step is ProviderStepViewModel { CurrentSubStep: 3 or 5 or 6 })
            {
                var providerView = ViewModel.StepViews["provider"];
                return providerView.BuildContent(step, CreateCallbacks());
            }

            // Normal: clear state, build fresh
            _stepSubs.Clear();
            var currentView = ViewModel.StepViews[step.StepId];
            currentView.ClearFocusState();
            return currentView.BuildContent(step, CreateCallbacks());
        });

        return _stepContentNode;
    }

    private LayoutNode BuildHelpText()
    {
        _helpTextNode = new DynamicLayoutNode(() =>
        {
            var step = ViewModel.Orchestrator.CurrentStep;
            var text = step?.GetHelpText() ?? "";
            return (ILayoutNode)new TextNode(text).WithForeground(Color.Gray);
        });

        return _helpTextNode.Height(2);
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.Context.StatusMessage
            .Select(msg => (ILayoutNode)(string.IsNullOrWhiteSpace(msg)
                ? Layouts.Empty()
                : new TextNode($"  {msg}").WithForeground(Color.Green)))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return Observable.CombineLatest(ViewModel.Orchestrator.CurrentStepIndex, ViewModel.IsComplete,
                (_, complete) =>
                {
                    if (complete)
                        return (ILayoutNode)new TextNode(
                            " [Enter] Exit  [Ctrl+Q] Quit").WithForeground(Color.BrightBlack);

                    var backLabel = ViewModel.Orchestrator.CurrentStepIndex.Value == 0 ? "Quit" : "Back";
                    return (ILayoutNode)new TextNode(
                        $" [\u2191/\u2193] Navigate  [Enter] Next  [Esc] {backLabel}  [Ctrl+Q] Quit").WithForeground(Color.BrightBlack);
                })
            .AsLayout()
            .Height(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Input handling
    // ═══════════════════════════════════════════════════════════════════

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        // Escape: go back (orchestrator handles sub-step back internally)
        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (!ViewModel.GoBack())
                ViewModel.RequestQuit();
            return;
        }

        var currentStep = ViewModel.Orchestrator.CurrentStep;

        // Channels step: custom keyboard handling (delegated to view)
        if (currentStep is ChannelsStepViewModel)
        {
            var channelsView = ViewModel.StepViews["channels"];
            if (channelsView.HandleKeyPress(key))
            {
                ViewModel.RequestRedraw();
                return;
            }
        }

        // Provider step special keys
        if (currentStep is ProviderStepViewModel providerVm)
        {
            // Browser OAuth: "C" to copy URL to clipboard
            if (providerVm.CurrentSubStep == 6
                && keyInfo.Key == ConsoleKey.C
                && providerVm.OAuth.BrowserOpenFailed
                && providerVm.OAuth.VerificationUri is not null)
            {
                if (OAuthFlowViews.TryCopyToClipboard(_clipboardService, providerVm.OAuth.VerificationUri))
                    ViewModel.Context.StatusMessage.Value = "\u2714 URL copied to clipboard";
                return;
            }

            // Validation sub-step with failed result: Enter retries, M goes to manual model entry
            if (providerVm.CurrentSubStep == 3 && providerVm.ProbeResult.Value is { Success: false })
            {
                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    providerVm.StartProbe();
                    _stepContentNode?.Invalidate();
                    _helpTextNode?.Invalidate();
                    ViewModel.RequestRedraw();
                    return;
                }

                if (keyInfo.Key == ConsoleKey.M)
                {
                    providerVm.SetSubStep(4);
                    _stepContentNode?.Invalidate();
                    _helpTextNode?.Invalidate();
                    ViewModel.RequestRedraw();
                    return;
                }
            }
        }

        // Health check step: Enter triggers the check or exits
        if (currentStep is HealthCheckStepViewModel healthVm && keyInfo.Key == ConsoleKey.Enter)
        {
            if (healthVm.IsComplete.Value)
                ViewModel.RequestQuit();
            else
                ViewModel.GoNext();
            return;
        }

        // Route to the current step view's HandleKeyPress
        if (currentStep is not null && ViewModel.StepViews.TryGetValue(currentStep.StepId, out var view))
        {
            view.HandleKeyPress(key);
            ViewModel.RequestRedraw();
        }
    }

    private void HandlePaste(PasteEvent paste)
    {
        var currentStep = ViewModel.Orchestrator.CurrentStep;
        if (currentStep is not null && ViewModel.StepViews.TryGetValue(currentStep.StepId, out var view))
        {
            view.HandlePaste(paste);
            ViewModel.RequestRedraw();
        }
    }

    private StepViewCallbacks CreateCallbacks()
    {
        return new StepViewCallbacks
        {
            Subscriptions = _stepSubs,
            InvalidateContent = () => _stepContentNode?.Invalidate(),
            InvalidateHelp = () => _helpTextNode?.Invalidate(),
            AdvanceStep = () => ViewModel.GoNext(),
            RequestRedraw = ViewModel.RequestRedraw,
        };
    }

    public override void Dispose()
    {
        _stepSubs.Dispose();
        base.Dispose();
    }
}
