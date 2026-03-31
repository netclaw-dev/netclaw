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
    private SelectionListNode<string>? _backendList;
    private TextInputNode? _braveApiKeyInput;
    private TextInputNode? _searxngEndpointInput;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;

    public string StepId => "search";

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
        var duckDuckGoLabel = "DuckDuckGo (default \u2014 no config needed, may hit bot detection)";
        var braveLabel = "Brave Search (API key required \u2014 reliable, fast)";
        var searxngLabel = "SearXNG (self-hosted \u2014 endpoint required)";

        _backendList = Layouts.SelectionList(duckDuckGoLabel, braveLabel, searxngLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _backendList.OnFocused();
        _lastFocusedList = _backendList;
        _lastFocusedInput = null;

        _backendList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var choice = selected[0];
                    if (choice == duckDuckGoLabel)
                    {
                        vm.SelectedBackend = SearchBackend.DuckDuckGo;
                        callbacks.AdvanceStep(); // step complete, no credentials needed
                    }
                    else if (choice == braveLabel)
                    {
                        vm.SelectedBackend = SearchBackend.Brave;
                        callbacks.AdvanceStep(); // → sub-step 1 (handled by TryAdvance)
                    }
                    else if (choice == searxngLabel)
                    {
                        vm.SelectedBackend = SearchBackend.SearXng;
                        callbacks.AdvanceStep(); // → sub-step 1
                    }
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose your web search provider:").WithForeground(Color.White))
            .WithChild(_backendList);
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
                .WithChild(new PanelNode()
                    .WithTitle("API Key")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Gray)
                    .WithContent(_braveApiKeyInput)
                    .Height(3));
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
            .WithChild(new PanelNode()
                .WithTitle("Endpoint")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_searxngEndpointInput)
                .Height(3));
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
