// -----------------------------------------------------------------------
// <copyright file="ProviderStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using R3;
using Termina.Clipboard;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Provider wizard step.
/// 7 sub-steps: provider selection → auth → credentials → validation → model → OAuth device → OAuth browser.
/// </summary>
public sealed class ProviderStepView : IWizardStepView
{
    private const int MaxDisplayedModels = 30;
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2838", "\u2834", "\u2826", "\u2807"];

    private readonly IClipboardService? _clipboardService;

    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _authMethodList;
    private TextInputNode? _apiKeyInput;
    private TextInputNode? _endpointInput;
    private SelectionListNode<string>? _modelList;
    private TextInputNode? _manualModelInput;
    private TextInputNode? _redirectUrlInput;
    private bool _manualModelEntry;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;
    private ProviderStepViewModel? _vm;

    public ProviderStepView(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
    }

    public string StepId => WizardStepIds.Provider;
    public bool ManagesOwnFocusState => (_vm?.CurrentSubStep ?? 0) is 3 or 5 or 6;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (ProviderStepViewModel)stepVm;
        _vm = vm;

        return vm.CurrentSubStep switch
        {
            0 => BuildProviderSelection(vm, callbacks),
            1 => BuildAuthMethodSelection(vm, callbacks),
            2 => BuildCredentialInput(vm, callbacks),
            3 => BuildValidation(vm),
            4 => BuildModelSelection(vm, callbacks),
            5 => BuildOAuthDeviceFlow(vm),
            6 => BuildBrowserOAuthFlow(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildProviderSelection(ProviderStepViewModel vm, StepViewCallbacks callbacks)
    {
        var registry = vm.Registry;
        var displayToTypeKey = registry.KnownTypeKeys
            .ToDictionary(k => registry.Get(k).DisplayName, k => k);

        _providerList = Layouts.SelectionList(displayToTypeKey.Keys.ToList())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _providerList.OnFocused();
        _lastFocusedList = _providerList;
        _lastFocusedInput = null;

        _providerList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0 && displayToTypeKey.TryGetValue(selected[0], out var typeKey))
                {
                    vm.SelectedProviderType = typeKey;
                    var descriptor = registry.Get(typeKey);
                    if (descriptor.Auth.SupportedAuthMethods is [AuthMethod.None])
                    {
                        vm.SelectedAuthMethod = AuthMethod.None;
                        vm.SetSubStep(2);
                    }
                    else
                    {
                        vm.SetSubStep(1);
                    }
                    callbacks.InvalidateAndRedraw();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose your LLM provider:").WithForeground(Color.White))
            .WithChild(_providerList);
    }

    private ILayoutNode BuildAuthMethodSelection(ProviderStepViewModel vm, StepViewCallbacks callbacks)
    {
        var providerType = vm.SelectedProviderType ?? "unknown";
        var descriptor = vm.Registry.Get(providerType);
        var supportedMethods = OAuthFlowViews.BuildAuthMethodLabels(descriptor.Auth);

        _authMethodList = Layouts.SelectionList(supportedMethods)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _authMethodList.OnFocused();
        _lastFocusedList = _authMethodList;
        _lastFocusedInput = null;

        _authMethodList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var method = OAuthFlowViews.ParseAuthMethodLabel(selected[0], descriptor.Auth);
                    vm.SelectedAuthMethod = method;

                    if (method == AuthMethod.OAuthPkce)
                    {
                        vm.SetSubStep(6);
                        vm.StartBrowserOAuthFlow();
                    }
                    else if (method == AuthMethod.OAuthDevice)
                    {
                        vm.SetSubStep(5);
                        vm.StartOAuthFlow();
                    }
                    else
                    {
                        vm.SetSubStep(2);
                    }
                    callbacks.InvalidateAndRedraw();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Authentication for {descriptor.DisplayName}:").WithForeground(Color.White))
            .WithChild(_authMethodList);
    }

    private ILayoutNode BuildCredentialInput(ProviderStepViewModel vm, StepViewCallbacks callbacks)
    {
        var providerType = vm.SelectedProviderType ?? "unknown";
        var descriptor = vm.Registry.Get(providerType);
        var displayName = descriptor.DisplayName;

        _lastFocusedList = null;

        if (descriptor.Auth is EndpointOnlyAuth)
        {
            var defaultEndpoint = descriptor.DefaultEndpoint;
            _endpointInput = new TextInputNode().WithPlaceholder(defaultEndpoint);
            _endpointInput.Text = vm.EndpointInput ?? defaultEndpoint;
            _endpointInput.OnFocused();
            _lastFocusedInput = _endpointInput;

            _endpointInput.Submitted
                .Subscribe(text =>
                {
                    vm.EndpointInput = string.IsNullOrWhiteSpace(text) ? defaultEndpoint : text;
                    vm.SetSubStep(3);
                    vm.StartProbe();
                    callbacks.InvalidateAndRedraw();
                })
                .DisposeWith(callbacks.Subscriptions);

            return Layouts.Vertical()
                .WithChild(new TextNode($"  {displayName} endpoint:").WithForeground(Color.White))
                .WithChild(WizardStepHelpers.BuildTextInputPanel(_endpointInput, "Endpoint"));
        }

        _apiKeyInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder($"Enter {displayName} API key...");

        if (!string.IsNullOrWhiteSpace(vm.ApiKeyInput))
            _apiKeyInput.Text = vm.ApiKeyInput;

        _apiKeyInput.OnFocused();
        _lastFocusedInput = _apiKeyInput;

        _apiKeyInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                vm.ApiKeyInput = text;
                vm.SetSubStep(3);
                vm.StartProbe();
                callbacks.InvalidateAndRedraw();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {displayName} API key:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_apiKeyInput, "API Key"));
    }

    private ILayoutNode BuildValidation(ProviderStepViewModel vm)
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;

        var probeResult = vm.ProbeResult.Value;

        if (vm.IsProbing.Value || probeResult is null)
        {
            var elapsed = vm.ProbeElapsedSeconds.Value;
            var frame = SpinnerFrames[elapsed % SpinnerFrames.Length];
            var timerText = elapsed > 0 ? $" ({elapsed}s)" : "";
            var provider = vm.SelectedProviderType ?? "provider";

            return Layouts.Vertical()
                .WithChild(new TextNode($"  {frame} Validating connection to {provider}...{timerText}")
                    .WithForeground(Color.Yellow));
        }

        if (probeResult.Success)
        {
            var modelCount = probeResult.Models.Count;
            return Layouts.Vertical()
                .WithChild(new TextNode($"  \u2713 Connected! Found {modelCount} model{(modelCount == 1 ? "" : "s")}.")
                    .WithForeground(Color.Green));
        }

        return Layouts.Vertical()
            .WithChild(new TextNode($"  \u2717 {probeResult.ErrorMessage}").WithForeground(Color.Red))
            .WithChild(new TextNode(""))
            .WithChild(new TextNode("  Press Enter to retry, M for manual model entry, or Esc to go back.")
                .WithForeground(Color.BrightBlack));
    }

    private ILayoutNode BuildModelSelection(ProviderStepViewModel vm, StepViewCallbacks callbacks)
    {
        _lastFocusedInput = null;

        if (_manualModelEntry)
            return BuildManualModelInput(vm, callbacks);

        var models = vm.DiscoveredModels;
        var items = new List<string>();

        var displayCount = Math.Min(models.Count, MaxDisplayedModels);
        for (var i = 0; i < displayCount; i++)
            items.Add(models[i].ModelId);

        if (models.Count > MaxDisplayedModels)
            items.Add($"... and {models.Count - MaxDisplayedModels} more (enter manually)");

        items.Add("Enter model ID manually...");

        _modelList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _modelList.OnFocused();
        _lastFocusedList = _modelList;

        _modelList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var choice = selected[0];
                    if (choice == "Enter model ID manually..." || choice.StartsWith("... and ", StringComparison.Ordinal))
                    {
                        _manualModelEntry = true;
                        callbacks.InvalidateContent();
                        callbacks.RequestRedraw();
                    }
                    else
                    {
                        vm.SelectedModelId = choice;
                        callbacks.AdvanceStep();
                    }
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        var header = models.Count > 0
            ? $"  Select a model ({models.Count} available):"
            : "  No models discovered. Enter a model ID manually:";

        return Layouts.Vertical()
            .WithChild(new TextNode(header).WithForeground(Color.White))
            .WithChild(_modelList);
    }

    private ILayoutNode BuildManualModelInput(ProviderStepViewModel vm, StepViewCallbacks callbacks)
    {
        _lastFocusedList = null;

        _manualModelInput = new TextInputNode()
            .WithPlaceholder("e.g., anthropic/claude-sonnet-4-20250514");

        _manualModelInput.OnFocused();
        _lastFocusedInput = _manualModelInput;

        _manualModelInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                vm.SelectedModelId = text;
                _manualModelEntry = false;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enter model ID:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_manualModelInput, "Model ID"));
    }

    private ILayoutNode BuildOAuthDeviceFlow(ProviderStepViewModel vm)
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;

        var providerType = vm.SelectedProviderType ?? "unknown";
        var descriptor = vm.Registry.Get(providerType);
        var flowState = vm.OAuth.FlowState.Value;

        var children = Layouts.Vertical();
        children.WithChild(new TextNode($"  OAuth Device Flow for {descriptor.DisplayName}")
            .WithForeground(Color.White).Bold());
        children.WithChild(new TextNode("").Height(1));

        switch (flowState)
        {
            case DeviceFlowState.NotStarted:
                children.WithChild(new TextNode("  Starting device authorization...")
                    .WithForeground(Color.Yellow));
                break;

            case DeviceFlowState.WaitingForUser:
            case DeviceFlowState.Polling:
            {
                var elapsed = vm.ProbeElapsedSeconds.Value;
                var frame = SpinnerFrames[elapsed % SpinnerFrames.Length];

                if (vm.OAuth.VerificationUri is not null)
                {
                    children.WithChild(new TextNode($"  Visit: {vm.OAuth.VerificationUri}")
                        .WithForeground(Color.Cyan));
                    children.WithChild(new TextNode("").Height(1));
                }
                if (vm.OAuth.UserCode is not null)
                {
                    children.WithChild(new TextNode($"  Enter code: {vm.OAuth.UserCode}")
                        .WithForeground(Color.White).Bold());
                    children.WithChild(new TextNode("").Height(1));
                }
                children.WithChild(new TextNode($"  {frame} Waiting for authorization...")
                    .WithForeground(Color.Yellow));
                break;
            }

            case DeviceFlowState.Succeeded:
                children.WithChild(new TextNode("  \u2713 Authorization successful!")
                    .WithForeground(Color.Green));
                break;

            case DeviceFlowState.Denied:
            case DeviceFlowState.Expired:
            case DeviceFlowState.Error:
                children.WithChild(new TextNode($"  \u2717 {vm.OAuth.ErrorMessage ?? "Authorization failed."}")
                    .WithForeground(Color.Red));
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode("  Press [Esc] to go back and try again.")
                    .WithForeground(Color.BrightBlack));
                break;

            case DeviceFlowState.Cancelled:
                children.WithChild(new TextNode("  Authorization cancelled.")
                    .WithForeground(Color.Yellow));
                break;
        }

        return children;
    }

    private ILayoutNode BuildBrowserOAuthFlow(ProviderStepViewModel vm, StepViewCallbacks callbacks)
    {
        var providerType = vm.SelectedProviderType ?? "unknown";
        var result = OAuthFlowViews.BuildBrowserOAuthFlow(
            vm.Registry.Get(providerType).DisplayName,
            vm.OAuth.FlowState.Value,
            vm.OAuth.BrowserOpenFailed,
            vm.OAuth.VerificationUri,
            vm.SpinnerTick.Value,
            vm.ProbeElapsedSeconds.Value,
            vm.OAuth.ErrorMessage,
            _clipboardService,
            ref _redirectUrlInput,
            text => _ = vm.SubmitRedirectUrlAsync(text));

        if (_redirectUrlInput is not null)
        {
            _lastFocusedInput = _redirectUrlInput;
            _redirectUrlInput.OnFocused();
        }

        return result;
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
        _providerList = null;
        _authMethodList = null;
        _apiKeyInput = null;
        _endpointInput = null;
        _modelList = null;
        _manualModelInput = null;
        _redirectUrlInput = null;
        _manualModelEntry = false;
    }
}
