// -----------------------------------------------------------------------
// <copyright file="BrowserAutomationStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private IDisposable? _enabledList;
    private IDisposable? _backendList;
    private IFocusable? _lastFocusedList;

    public string StepId => WizardStepIds.BrowserAutomation;

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
        var noOption = new SelectionOption<bool>(false, "No — skip browser automation for now");
        var yesOption = new SelectionOption<bool>(true, "Yes — configure browser MCP tools");

        var enabledList = Layouts.SelectionList<SelectionOption<bool>>(
                [noOption, yesOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _enabledList = enabledList;
        enabledList.OnFocused();
        _lastFocusedList = enabledList;

        enabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                vm.Enabled = selected[0].Value;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable browser automation MCP tools?").WithForeground(Color.White))
            .WithChild(enabledList);
    }

    private ILayoutNode BuildBackendSubStep(BrowserAutomationStepViewModel vm, StepViewCallbacks callbacks)
    {
        var chromeLabel = vm.IsChromeDevToolsAvailable
            ? "Chrome DevTools MCP"
            : $"Chrome DevTools MCP (disabled - {vm.ChromeDevToolsUnavailableReason})";
        var chromeOption = new SelectionOption<BrowserAutomationBackend>(BrowserAutomationBackend.ChromeDevTools, chromeLabel);
        var playwrightOption = new SelectionOption<BrowserAutomationBackend>(BrowserAutomationBackend.Playwright, "Playwright MCP");

        var backendList = Layouts.SelectionList<SelectionOption<BrowserAutomationBackend>>(
                [chromeOption, playwrightOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _backendList = backendList;
        backendList.OnFocused();
        _lastFocusedList = backendList;

        backendList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                if (selected[0].Value == BrowserAutomationBackend.ChromeDevTools && !vm.IsChromeDevToolsAvailable)
                {
                    // Can't select disabled option
                    return;
                }

                vm.SelectedBackend = selected[0].Value;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose browser MCP backend:").WithForeground(Color.White))
            .WithChild(backendList);
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
