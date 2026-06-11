// -----------------------------------------------------------------------
// <copyright file="InitWizardPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private readonly CompositeDisposable _stepSubs = [];

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

        // Provider step: OAuth flow state changes
        ViewModel.ProviderStep.OAuth.FlowState
            .Subscribe(state =>
            {
                if (ViewModel.Orchestrator.CurrentStep is not ProviderStepViewModel { CurrentSubStep: 5 or 6 })
                    return;

                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();

                // Auto-advance to validation on success.
                // The coordinator's onSuccess callback (wired in
                // ProviderStepViewModel.StartOAuthFlow) already calls
                // StartProbe() with the fresh token, so we MUST NOT call it
                // here too — doing so cancels the probe that onSuccess just
                // started (this subscriber fires synchronously from
                // FlowState.Value = Succeeded, which runs *before* the
                // coordinator invokes onSuccess; the duplicate StartProbe
                // races and torpedoes its own CTS).
                if (state == DeviceFlowState.Succeeded)
                {
                    ViewModel.ProviderStep.SetSubStep(3);
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

            var currentView = ViewModel.StepViews[step.StepId];
            // Views with ManagesOwnFocusState = true must clear
            // callbacks.Subscriptions themselves to prevent subscription
            // accumulation on cursor-blink-timer re-renders (#792).
            if (!currentView.ManagesOwnFocusState)
            {
                _stepSubs.Clear();
                currentView.ClearFocusState();
            }
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

    /// <summary>
    /// Capture-phase input handler — runs BEFORE the focus manager routes keys
    /// to focused components. This prevents stale IFocusable nodes (SelectionListNode,
    /// TextInputNode) from consuming keys meant for steps with custom key handling.
    /// </summary>
    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        // Let base key bindings run first
        if (base.HandlePageInput(keyInfo))
            return true;

        // Global: Ctrl+Q always shuts down — must be captured here so stale
        // focused components (TextInputNode in add mode) can't consume it.
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return true;
        }

        var currentStep = ViewModel.Orchestrator.CurrentStep;
        if (currentStep is not null
            && ViewModel.StepViews.TryGetValue(currentStep.StepId, out var captureView)
            && captureView.CapturesInput
            && captureView.HandleKeyPress(new KeyPressed(keyInfo)))
        {
            ViewModel.RequestRedraw();
            return true;
        }

        return false;
    }

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

            // Device OAuth: "C" to copy user code to clipboard
            if (providerVm.CurrentSubStep == 5
                && keyInfo.Key == ConsoleKey.C
                && providerVm.OAuth.UserCode is not null)
            {
                if (OAuthFlowViews.TryCopyToClipboard(_clipboardService, providerVm.OAuth.UserCode))
                {
                    ViewModel.Context.StatusMessage.Value = "\u2714 Code copied to clipboard";
                }
                return;
            }

            // Device OAuth: "O" to open the verification URL in the default browser
            if (providerVm.CurrentSubStep == 5
                && keyInfo.Key == ConsoleKey.O
                && (providerVm.OAuth.VerificationUriComplete ?? providerVm.OAuth.VerificationUri) is not null)
            {
                var url = providerVm.OAuth.VerificationUriComplete ?? providerVm.OAuth.VerificationUri;
                ViewModel.Context.StatusMessage.Value = OAuthFlowViews.TryOpenInBrowser(url)
                    ? "\u2714 Opening browser..."
                    : "\u2718 Could not open browser.";
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
