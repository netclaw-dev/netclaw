using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the BrowserAutomation wizard step.
/// Sub-step 0: enable/disable selection. Sub-step 1: backend selection.
/// </summary>
public sealed class BrowserAutomationStepView : IWizardStepView
{
    private SelectionListNode<string>? _enabledList;
    private SelectionListNode<string>? _backendList;
    private IFocusable? _lastFocusedList;

    public string StepId => "browser-automation";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (BrowserAutomationStepViewModel)stepVm;

        return vm.CurrentSubStep switch
        {
            0 => BuildEnableSubStep(vm, callbacks),
            1 => BuildBackendSubStep(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildEnableSubStep(BrowserAutomationStepViewModel vm, StepViewCallbacks callbacks)
    {
        _enabledList = Layouts.SelectionList(
                "No \u2014 skip browser automation for now",
                "Yes \u2014 configure browser MCP tools")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _enabledList.OnFocused();
        _lastFocusedList = _enabledList;

        _enabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                if (selected[0].StartsWith("Yes", StringComparison.Ordinal))
                {
                    vm.Enabled = true;
                    callbacks.AdvanceStep(); // → sub-step 1 via TryAdvance
                }
                else
                {
                    vm.Enabled = false;
                    callbacks.AdvanceStep(); // step complete
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable browser automation MCP tools?").WithForeground(Color.White))
            .WithChild(_enabledList);
    }

    private ILayoutNode BuildBackendSubStep(BrowserAutomationStepViewModel vm, StepViewCallbacks callbacks)
    {
        var chromeLabel = vm.IsChromeDevToolsAvailable
            ? "Chrome DevTools MCP"
            : $"Chrome DevTools MCP (disabled - {vm.ChromeDevToolsUnavailableReason})";

        _backendList = Layouts.SelectionList(chromeLabel, "Playwright MCP")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _backendList.OnFocused();
        _lastFocusedList = _backendList;

        _backendList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                if (selected[0].StartsWith("Chrome DevTools", StringComparison.Ordinal)
                    && !vm.IsChromeDevToolsAvailable)
                {
                    // Can't select disabled option — show error
                    return;
                }

                vm.SelectedBackend = selected[0].StartsWith("Playwright", StringComparison.Ordinal)
                    ? BrowserAutomationBackend.Playwright
                    : BrowserAutomationBackend.ChromeDevTools;

                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose browser MCP backend:").WithForeground(Color.White))
            .WithChild(_backendList);
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_lastFocusedList is not null)
        {
            _lastFocusedList.HandleInput(key.KeyInfo);
            return true;
        }
        return false;
    }

    public void HandlePaste(PasteEvent paste) { }

    public void ClearFocusState()
    {
        _lastFocusedList = null;
        _enabledList = null;
        _backendList = null;
    }
}
