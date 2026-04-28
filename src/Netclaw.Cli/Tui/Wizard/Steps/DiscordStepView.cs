// -----------------------------------------------------------------------
// <copyright file="DiscordStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Discord wizard step.
/// 6 sub-steps: enable -> bot token -> channel IDs -> DM enabled -> user access choice -> allowed user IDs (conditional).
/// </summary>
public sealed class DiscordStepView : IWizardStepView
{
    private SelectionListNode<string>? _enabledList;
    private TextInputNode? _botTokenInput;
    private TextInputNode? _channelIdsInput;
    private SelectionListNode<string>? _dmEnabledList;
    private SelectionListNode<string>? _userAccessChoiceList;
    private TextInputNode? _allowedUserIdsInput;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;

    public string StepId => "discord";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (DiscordStepViewModel)stepVm;

        return vm.CurrentSubStep switch
        {
            0 => BuildEnableSubStep(vm, callbacks),
            1 => BuildBotTokenSubStep(vm, callbacks),
            2 => BuildChannelIdsSubStep(vm, callbacks),
            3 => BuildDmEnabledSubStep(vm, callbacks),
            4 => BuildUserAccessChoiceSubStep(vm, callbacks),
            5 => BuildAllowedUserIdsSubStep(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildEnableSubStep(DiscordStepViewModel vm, StepViewCallbacks callbacks)
    {
        var yesLabel = "Yes - configure Discord bot";
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

                vm.DiscordEnabled = selected[0] == yesLabel;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable Discord integration?").WithForeground(Color.White))
            .WithChild(_enabledList);
    }

    private ILayoutNode BuildBotTokenSubStep(DiscordStepViewModel vm, StepViewCallbacks callbacks)
    {
        _botTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("Discord bot token");

        _botTokenInput.OnFocused();
        _lastFocusedInput = _botTokenInput;
        _lastFocusedList = null;

        _botTokenInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                vm.BotToken = text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Discord Bot Token:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Bot Token")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_botTokenInput)
                .Height(3));
    }

    private ILayoutNode BuildChannelIdsSubStep(DiscordStepViewModel vm, StepViewCallbacks callbacks)
    {
        _channelIdsInput = new TextInputNode()
            .WithPlaceholder("123456789012345678, 223456789012345678  (leave blank to skip)");

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
            .WithChild(new PanelNode()
                .WithTitle("Channel IDs")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_channelIdsInput)
                .Height(3));
    }

    private ILayoutNode BuildDmEnabledSubStep(DiscordStepViewModel vm, StepViewCallbacks callbacks)
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

    private ILayoutNode BuildUserAccessChoiceSubStep(DiscordStepViewModel vm, StepViewCallbacks callbacks)
    {
        var (list, layout) = WizardStepHelpers.BuildUserAccessChoiceSubStep(
            restrict => vm.RestrictToSpecificUsers = restrict, callbacks);

        _userAccessChoiceList = list;
        _lastFocusedList = list;
        _lastFocusedInput = null;

        return layout;
    }

    private ILayoutNode BuildAllowedUserIdsSubStep(DiscordStepViewModel vm, StepViewCallbacks callbacks)
    {
        _allowedUserIdsInput = new TextInputNode()
            .WithPlaceholder("129847561203948576, 130111223344556677  (Discord user IDs)");

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
            .WithChild(new PanelNode()
                .WithTitle("Allowed User IDs")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_allowedUserIdsInput)
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
        _enabledList = null;
        _botTokenInput = null;
        _channelIdsInput = null;
        _dmEnabledList = null;
        _userAccessChoiceList = null;
        _allowedUserIdsInput = null;
    }
}
