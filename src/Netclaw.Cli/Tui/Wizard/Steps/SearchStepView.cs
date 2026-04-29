// -----------------------------------------------------------------------
// <copyright file="SearchStepView.cs" company="Petabridge, LLC">
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

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Search wizard step.
/// Sub-step 0: backend selection list. Sub-step 1: credentials input.
/// </summary>
public sealed class SearchStepView : IWizardStepView
{
    private IDisposable? _backendList;
    private TextInputNode? _braveApiKeyInput;
    private TextInputNode? _searxngEndpointInput;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;

    public string StepId => WizardStepIds.Search;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (SearchStepViewModel)stepVm;

        return vm.CurrentSubStep switch
        {
            0 => BuildBackendSelection(vm, callbacks),
            1 => BuildCredentialInput(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildBackendSelection(SearchStepViewModel vm, StepViewCallbacks callbacks)
    {
        var duckDuckGoOption = new SelectionOption<SearchBackend>(SearchBackend.DuckDuckGo,
            "DuckDuckGo (default — no config needed, may hit bot detection)");
        var braveOption = new SelectionOption<SearchBackend>(SearchBackend.Brave,
            "Brave Search (API key required — reliable, fast)");
        var searxngOption = new SelectionOption<SearchBackend>(SearchBackend.SearXng,
            "SearXNG (self-hosted — endpoint required)");

        var backendList = Layouts.SelectionList<SelectionOption<SearchBackend>>(
                [duckDuckGoOption, braveOption, searxngOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _backendList = backendList;
        backendList.OnFocused();
        _lastFocusedList = backendList;
        _lastFocusedInput = null;

        backendList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.SelectedBackend = selected[0].Value;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose your web search provider:").WithForeground(Color.White))
            .WithChild(backendList);
    }

    private ILayoutNode BuildCredentialInput(SearchStepViewModel vm, StepViewCallbacks callbacks)
    {
        _lastFocusedList = null;

        if (vm.SelectedBackend == SearchBackend.Brave)
        {
            _braveApiKeyInput = new TextInputNode()
                .AsPassword()
                .WithPlaceholder("Enter Brave Search API key...");

            if (!string.IsNullOrWhiteSpace(vm.BraveApiKey))
                _braveApiKeyInput.Text = vm.BraveApiKey;

            _braveApiKeyInput.OnFocused();
            _lastFocusedInput = _braveApiKeyInput;

            _braveApiKeyInput.Submitted
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Subscribe(text =>
                {
                    vm.BraveApiKey = text;
                    callbacks.AdvanceStep();
                })
                .DisposeWith(callbacks.Subscriptions);

            return Layouts.Vertical()
                .WithChild(new TextNode("  Brave Search API key:").WithForeground(Color.White))
                .WithChild(WizardStepHelpers.BuildTextInputPanel(_braveApiKeyInput, "API Key"));
        }

        // SearXNG
        _searxngEndpointInput = new TextInputNode()
            .WithPlaceholder("http://searxng.local:8080");

        if (!string.IsNullOrWhiteSpace(vm.SearXngEndpoint))
            _searxngEndpointInput.Text = vm.SearXngEndpoint;

        _searxngEndpointInput.OnFocused();
        _lastFocusedInput = _searxngEndpointInput;

        _searxngEndpointInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                vm.SearXngEndpoint = text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  SearXNG endpoint URL:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_searxngEndpointInput, "Endpoint"));
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_lastFocusedList is not null)
        {
            _lastFocusedList.HandleInput(key.KeyInfo);
            return true;
        }
        if (_lastFocusedInput is not null)
        {
            _lastFocusedInput.HandleInput(key.KeyInfo);
            return true;
        }
        return false;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _lastFocusedInput?.HandlePaste(paste);
    }

    public void ClearFocusState()
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;
        _backendList = null;
        _braveApiKeyInput = null;
        _searxngEndpointInput = null;
    }
}
