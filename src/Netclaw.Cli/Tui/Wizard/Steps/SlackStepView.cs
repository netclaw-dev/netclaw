// -----------------------------------------------------------------------
// <copyright file="SlackStepView.cs" company="Petabridge, LLC">
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
/// Termina view for the Slack wizard step.
/// 7 sub-steps: enable → bot token → app token → channel names → DM → user access choice → user IDs (conditional).
/// </summary>
public sealed class SlackStepView : IWizardStepView
{
    private SlackStepViewModel? _vm;
    private SelectionListNode<string>? _enabledList;
    private TextInputNode? _botTokenInput;
    private TextInputNode? _appTokenInput;
    private TextInputNode? _channelNamesInput;
    private SelectionListNode<string>? _dmEnabledList;
    private IDisposable? _userAccessChoiceList;
    private TextInputNode? _allowedUserIdsInput;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;

    public string StepId => WizardStepIds.Slack;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (SlackStepViewModel)stepVm;
        _vm = vm;

        return vm.CurrentSubStep switch
        {
            0 => BuildEnableSubStep(vm, callbacks),
            1 => BuildBotTokenSubStep(vm, callbacks),
            2 => BuildAppTokenSubStep(vm, callbacks),
            3 => BuildChannelNamesSubStep(vm, callbacks),
            4 => BuildDmEnabledSubStep(vm, callbacks),
            5 => BuildUserAccessChoiceSubStep(vm, callbacks),
            6 => BuildAllowedUserIdsSubStep(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildEnableSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        var yesLabel = "Yes \u2014 configure Slack bot";
        var noLabel = "No \u2014 skip for now";

        _enabledList = Layouts.SelectionList(yesLabel, noLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _enabledList.OnFocused();
        _lastFocusedList = _enabledList;
        _lastFocusedInput = null;

        _enabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.SlackEnabled = selected[0] == yesLabel;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable Slack integration?").WithForeground(Color.White))
            .WithChild(_enabledList);
    }

    private ILayoutNode BuildBotTokenSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        _botTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("xoxb-...");
        WizardStepHelpers.SeedTextInput(_botTokenInput, vm.BotTokenDraft ?? vm.BotToken);

        _botTokenInput.OnFocused();
        _lastFocusedInput = _botTokenInput;
        _lastFocusedList = null;
        WizardStepHelpers.SyncInputToViewModel(_botTokenInput, StageFocusedInput, callbacks);

        _botTokenInput.Submitted
            .Subscribe(text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    vm.BotTokenDraft = null;
                    if (vm.HasPersistedBotToken || !string.IsNullOrWhiteSpace(vm.BotToken))
                    {
                        callbacks.ClearStatusMessage();
                        callbacks.AdvanceStep();
                    }
                    else
                    {
                        callbacks.ShowValidationError(ChannelsEditorValidationMessages.SlackBotTokenRequired);
                    }

                    return;
                }

                if (!text.StartsWith("xoxb-", StringComparison.OrdinalIgnoreCase))
                {
                    callbacks.ShowValidationError(ChannelsEditorValidationMessages.SlackBotTokenPrefix);
                    return;
                }
                vm.BotToken = text;
                vm.BotTokenDraft = text;
                callbacks.ClearStatusMessage();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Slack Bot Token:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_botTokenInput, "Bot Token"));

        if (vm.HasPersistedBotToken)
            layout = layout.WithChild(new TextNode("  (configured - leave blank to keep)").WithForeground(Color.BrightBlack));

        return layout;
    }

    private ILayoutNode BuildAppTokenSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        _appTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("xapp-...");
        WizardStepHelpers.SeedTextInput(_appTokenInput, vm.AppTokenDraft ?? vm.AppToken);

        _appTokenInput.OnFocused();
        _lastFocusedInput = _appTokenInput;
        _lastFocusedList = null;
        WizardStepHelpers.SyncInputToViewModel(_appTokenInput, StageFocusedInput, callbacks);

        _appTokenInput.Submitted
            .Subscribe(text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    vm.AppTokenDraft = null;
                    if (vm.HasPersistedAppToken || !string.IsNullOrWhiteSpace(vm.AppToken))
                    {
                        callbacks.ClearStatusMessage();
                        callbacks.AdvanceStep();
                    }
                    else
                    {
                        callbacks.ShowValidationError(ChannelsEditorValidationMessages.SlackAppTokenRequired);
                    }

                    return;
                }

                if (!text.StartsWith("xapp-", StringComparison.OrdinalIgnoreCase))
                {
                    callbacks.ShowValidationError(ChannelsEditorValidationMessages.SlackAppTokenPrefix);
                    return;
                }
                vm.AppToken = text;
                vm.AppTokenDraft = text;
                callbacks.ClearStatusMessage();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Slack App Token (Socket Mode):").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_appTokenInput, "App Token"));

        if (vm.HasPersistedAppToken)
            layout = layout.WithChild(new TextNode("  (configured - leave blank to keep)").WithForeground(Color.BrightBlack));

        return layout;
    }

    private ILayoutNode BuildChannelNamesSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        _channelNamesInput = new TextInputNode()
            .WithPlaceholder("general, dev, random  (leave blank to skip)");
        WizardStepHelpers.SeedTextInput(_channelNamesInput, vm.ChannelNamesInput);

        _channelNamesInput.OnFocused();
        _lastFocusedInput = _channelNamesInput;
        _lastFocusedList = null;
        WizardStepHelpers.SyncInputToViewModel(_channelNamesInput, StageFocusedInput, callbacks);

        _channelNamesInput.Submitted
            .Subscribe(text =>
            {
                vm.ChannelNamesInput = string.IsNullOrWhiteSpace(text) ? null : text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Channel names (press Enter to skip):").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_channelNamesInput, "Channel Names"));
    }

    private ILayoutNode BuildDmEnabledSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        var dmYesLabel = "Yes \u2014 allow approved users to DM the bot";
        var dmNoLabel = "No \u2014 channel messages only (default)";

        _dmEnabledList = Layouts.SelectionList(dmYesLabel, dmNoLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _dmEnabledList.OnFocused();
        _lastFocusedList = _dmEnabledList;
        _lastFocusedInput = null;

        _dmEnabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.AllowDirectMessages = selected[0] == dmYesLabel;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Allow direct messages?").WithForeground(Color.White))
            .WithChild(_dmEnabledList);
    }

    private ILayoutNode BuildUserAccessChoiceSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        var (list, layout) = WizardStepHelpers.BuildUserAccessChoiceSubStep(
            restrict => vm.RestrictToSpecificUsers = restrict, callbacks);

        _userAccessChoiceList = list;
        _lastFocusedList = list;
        _lastFocusedInput = null;

        return layout;
    }

    private ILayoutNode BuildAllowedUserIdsSubStep(SlackStepViewModel vm, StepViewCallbacks callbacks)
    {
        _allowedUserIdsInput = new TextInputNode()
            .WithPlaceholder("U01ABC123, U02DEF456  (Slack user IDs, comma-separated)");
        WizardStepHelpers.SeedTextInput(_allowedUserIdsInput, vm.AllowedUserIdsInput);

        _allowedUserIdsInput.OnFocused();
        _lastFocusedInput = _allowedUserIdsInput;
        _lastFocusedList = null;
        WizardStepHelpers.SyncInputToViewModel(_allowedUserIdsInput, StageFocusedInput, callbacks);

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
            if (key.KeyInfo.Key != ConsoleKey.Enter)
                StageFocusedInput();
            return true;
        }
        return false;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _lastFocusedInput?.HandlePaste(paste);
        StageFocusedInput();
    }

    private void StageFocusedInput()
    {
        if (_vm is null)
            return;

        if (ReferenceEquals(_lastFocusedInput, _botTokenInput))
            _vm.BotTokenDraft = _botTokenInput?.Text;
        else if (ReferenceEquals(_lastFocusedInput, _appTokenInput))
            _vm.AppTokenDraft = _appTokenInput?.Text;
        else if (ReferenceEquals(_lastFocusedInput, _channelNamesInput))
            _vm.ChannelNamesInput = _channelNamesInput?.Text;
        else if (ReferenceEquals(_lastFocusedInput, _allowedUserIdsInput))
            _vm.AllowedUserIdsInput = _allowedUserIdsInput?.Text;
    }

    public void ClearFocusState()
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;
        _enabledList = null;
        _botTokenInput = null;
        _appTokenInput = null;
        _channelNamesInput = null;
        _dmEnabledList = null;
        _userAccessChoiceList = null;
        _allowedUserIdsInput = null;
    }
}
