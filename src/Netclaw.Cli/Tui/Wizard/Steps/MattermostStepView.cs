// -----------------------------------------------------------------------
// <copyright file="MattermostStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Config;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Mattermost wizard step.
/// 8 sub-steps: enable -> server URL -> bot token -> channel IDs -> DM enabled ->
/// user access choice -> allowed user IDs (conditional) -> callback URL.
/// </summary>
public sealed class MattermostStepView : IWizardStepView
{
    private SelectionListNode<string>? _enabledList;
    private TextInputNode? _serverUrlInput;
    private TextInputNode? _botTokenInput;
    private TextInputNode? _channelIdsInput;
    private SelectionListNode<string>? _dmEnabledList;
    private IDisposable? _userAccessChoiceList;
    private TextInputNode? _allowedUserIdsInput;
    private TextInputNode? _callbackUrlInput;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;

    public string StepId => WizardStepIds.Mattermost;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (MattermostStepViewModel)stepVm;

        return vm.CurrentSubStep switch
        {
            0 => BuildEnableSubStep(vm, callbacks),
            1 => BuildServerUrlSubStep(vm, callbacks),
            2 => BuildBotTokenSubStep(vm, callbacks),
            3 => BuildChannelIdsSubStep(vm, callbacks),
            4 => BuildDmEnabledSubStep(vm, callbacks),
            5 => BuildUserAccessChoiceSubStep(vm, callbacks),
            6 => BuildAllowedUserIdsSubStep(vm, callbacks),
            7 => BuildCallbackUrlSubStep(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildEnableSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        var yesLabel = "Yes - configure Mattermost bot";
        var noLabel = "No - skip for now";

        _enabledList = Layouts.SelectionList(yesLabel, noLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _enabledList.OnFocused();
        _lastFocusedList = _enabledList;
        _lastFocusedInput = null;

        _enabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                vm.MattermostEnabled = selected[0] == yesLabel;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable Mattermost integration?").WithForeground(Color.White))
            .WithChild(_enabledList);
    }

    private ILayoutNode BuildServerUrlSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        _serverUrlInput = new TextInputNode()
            .WithPlaceholder("https://mm.example.com");

        if (!string.IsNullOrWhiteSpace(vm.ServerUrl))
            _serverUrlInput.Text = vm.ServerUrl;

        _serverUrlInput.OnFocused();
        _lastFocusedInput = _serverUrlInput;
        _lastFocusedList = null;

        _serverUrlInput.Submitted
            .Subscribe(text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    callbacks.ShowValidationError(ChannelsEditorValidationMessages.MattermostServerUrlRequired);
                    return;
                }

                if (!ChannelsEditorValidator.IsHttpUrl(text.Trim()))
                {
                    callbacks.ShowValidationError(ChannelsEditorValidationMessages.MattermostServerUrlAbsoluteHttp);
                    return;
                }

                vm.ServerUrl = text.Trim();
                callbacks.ClearStatusMessage();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Mattermost Server URL:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_serverUrlInput, "Server URL"));
    }

    private ILayoutNode BuildBotTokenSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        _botTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("Mattermost bot access token");

        _botTokenInput.OnFocused();
        _lastFocusedInput = _botTokenInput;
        _lastFocusedList = null;

        _botTokenInput.Submitted
            .Subscribe(text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (vm.HasPersistedBotToken || !string.IsNullOrWhiteSpace(vm.BotToken))
                    {
                        callbacks.ClearStatusMessage();
                        callbacks.AdvanceStep();
                    }
                    else
                    {
                        callbacks.ShowValidationError(ChannelsEditorValidationMessages.MattermostBotTokenRequired);
                    }

                    return;
                }

                vm.BotToken = text;
                callbacks.ClearStatusMessage();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Mattermost Bot Token:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_botTokenInput, "Bot Token"));

        if (vm.HasPersistedBotToken)
            layout = layout.WithChild(new TextNode("  (configured - leave blank to keep)").WithForeground(Color.BrightBlack));

        return layout;
    }

    private ILayoutNode BuildChannelIdsSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        _channelIdsInput = new TextInputNode()
            .WithPlaceholder("4xp9p3onpins8..., 9rp7q1...  (leave blank to skip)");

        if (!string.IsNullOrWhiteSpace(vm.ChannelIdsInput))
            _channelIdsInput.Text = vm.ChannelIdsInput;

        _channelIdsInput.OnFocused();
        _lastFocusedInput = _channelIdsInput;
        _lastFocusedList = null;

        _channelIdsInput.Submitted
            .Subscribe(text =>
            {
                vm.ChannelIdsInput = string.IsNullOrWhiteSpace(text) ? null : text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Allowed channel IDs (press Enter to skip):").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_channelIdsInput, "Channel IDs"));
    }

    private ILayoutNode BuildDmEnabledSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        var dmYesLabel = "Yes - allow approved users to DM the bot";
        var dmNoLabel = "No - channel messages only (default)";

        _dmEnabledList = Layouts.SelectionList(dmYesLabel, dmNoLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _dmEnabledList.OnFocused();
        _lastFocusedList = _dmEnabledList;
        _lastFocusedInput = null;

        _dmEnabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                vm.AllowDirectMessages = selected[0] == dmYesLabel;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Allow direct messages?").WithForeground(Color.White))
            .WithChild(_dmEnabledList);
    }

    private ILayoutNode BuildUserAccessChoiceSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        var (list, layout) = WizardStepHelpers.BuildUserAccessChoiceSubStep(
            restrict => vm.RestrictToSpecificUsers = restrict, callbacks);

        _userAccessChoiceList = list;
        _lastFocusedList = list;
        _lastFocusedInput = null;

        return layout;
    }

    private ILayoutNode BuildAllowedUserIdsSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        _allowedUserIdsInput = new TextInputNode()
            .WithPlaceholder("4xp9p3onpins8..., 9rp...  (Mattermost user IDs)");

        if (!string.IsNullOrWhiteSpace(vm.AllowedUserIdsInput))
            _allowedUserIdsInput.Text = vm.AllowedUserIdsInput;

        _allowedUserIdsInput.OnFocused();
        _lastFocusedInput = _allowedUserIdsInput;
        _lastFocusedList = null;

        _allowedUserIdsInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                vm.AllowedUserIdsInput = text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Allowed user IDs (at least one required):").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_allowedUserIdsInput, "Allowed User IDs"));
    }

    private ILayoutNode BuildCallbackUrlSubStep(MattermostStepViewModel vm, StepViewCallbacks callbacks)
    {
        _callbackUrlInput = new TextInputNode()
            .WithPlaceholder("https://netclaw.example.com/api/mattermost/actions  (leave blank to skip)");

        if (!string.IsNullOrWhiteSpace(vm.CallbackUrl))
            _callbackUrlInput.Text = vm.CallbackUrl;

        _callbackUrlInput.OnFocused();
        _lastFocusedInput = _callbackUrlInput;
        _lastFocusedList = null;

        _callbackUrlInput.Submitted
            .Subscribe(text =>
            {
                if (!string.IsNullOrWhiteSpace(text) && !ChannelsEditorValidator.IsHttpUrl(text.Trim()))
                {
                    callbacks.ShowValidationError(ChannelsEditorValidationMessages.MattermostCallbackUrlAbsoluteHttp);
                    return;
                }

                vm.CallbackUrl = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                callbacks.ClearStatusMessage();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Callback URL for interactive approvals (press Enter to skip):")
                .WithForeground(Color.White))
            .WithChild(new TextNode(
                    "  Optional. Required only for interactive approval buttons. The Mattermost\n" +
                    "  server must be able to reach this URL, so leave it blank unless the Netclaw\n" +
                    "  daemon's HTTP port is reachable from your Mattermost server (a non-Local\n" +
                    "  exposure mode). When blank, approvals use text replies.")
                .WithForeground(Color.BrightBlack))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_callbackUrlInput, "Callback URL"));
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
        _enabledList = null;
        _serverUrlInput = null;
        _botTokenInput = null;
        _channelIdsInput = null;
        _dmEnabledList = null;
        _userAccessChoiceList = null;
        _allowedUserIdsInput = null;
        _callbackUrlInput = null;
    }
}
