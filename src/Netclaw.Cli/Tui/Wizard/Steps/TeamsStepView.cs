// -----------------------------------------------------------------------
// <copyright file="TeamsStepView.cs" company="Petabridge, LLC">
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

public sealed class TeamsStepView : IWizardStepView
{
    private IFocusable? _focusedList;
    private TextInputBaseNode? _focusedInput;

    public string StepId => "teams";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (TeamsStepViewModel)stepVm;
        return vm.CurrentSubStep switch
        {
            0 => BuildEnabled(vm, callbacks),
            1 => BuildInput("  Entra tenant ID:", "tenant GUID", vm.TenantId, value => vm.TenantId = value, callbacks),
            2 => BuildInput("  Entra application / client ID:", "application GUID", vm.ClientId, value => vm.ClientId = value, callbacks),
            3 => BuildInput("  Teams bot ID:", "bot registration ID", vm.BotId, value => vm.BotId = value, callbacks),
            4 => BuildInput("  Teams client secret:", vm.HasPersistedClientSecret ? "configured - leave blank to keep" : "client secret", vm.ClientSecret, value => vm.ClientSecret = value, callbacks, isSecret: true, allowBlank: vm.HasPersistedClientSecret),
            5 => BuildInput("  Advanced: canonical Team IDs (comma-separated, Enter to skip):", "Team IDs", vm.TeamIdsInput, value => vm.TeamIdsInput = value, callbacks, allowBlank: true),
            6 => BuildInput("  Advanced: canonical channel IDs (comma-separated, Enter to skip):", "Channel IDs", vm.ChannelIdsInput, value => vm.ChannelIdsInput = value, callbacks, allowBlank: true),
            7 => BuildDirectMessages(vm, callbacks),
            8 => BuildInput("  Advanced: global Entra user IDs (comma-separated, Enter to skip):", "user object IDs", vm.AllowedUserIdsInput, value => vm.AllowedUserIdsInput = value, callbacks, allowBlank: true),
            9 => BuildInput("  Advanced: global Entra group IDs (comma-separated, Enter to skip):", "group object IDs", vm.AllowedGroupIdsInput, value => vm.AllowedGroupIdsInput = value, callbacks, allowBlank: true),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildEnabled(TeamsStepViewModel vm, StepViewCallbacks callbacks)
    {
        const string yes = "Yes - configure Microsoft Teams";
        const string no = "No - skip for now";
        var list = Layouts.SelectionList(yes, no).WithMode(SelectionMode.Single).WithHighlightColors(Color.Black, Color.Cyan);
        list.OnFocused();
        _focusedList = list;
        _focusedInput = null;
        list.SelectionConfirmed.Subscribe(selected =>
        {
            if (selected.Count == 0)
                return;
            vm.TeamsEnabled = selected[0] == yes;
            callbacks.AdvanceStep();
        }).DisposeWith(callbacks.Subscriptions);
        return Layouts.Vertical().WithChild(new TextNode("  Enable Microsoft Teams integration?").WithForeground(Color.White)).WithChild(list);
    }

    private ILayoutNode BuildDirectMessages(TeamsStepViewModel vm, StepViewCallbacks callbacks)
    {
        const string yes = "Yes - allow explicitly authorized direct messages";
        const string no = "No - channel messages only (default)";
        var list = Layouts.SelectionList(yes, no).WithMode(SelectionMode.Single).WithHighlightColors(Color.Black, Color.Cyan);
        list.OnFocused();
        _focusedList = list;
        _focusedInput = null;
        list.SelectionConfirmed.Subscribe(selected =>
        {
            if (selected.Count == 0)
                return;
            vm.AllowDirectMessages = selected[0] == yes;
            callbacks.AdvanceStep();
        }).DisposeWith(callbacks.Subscriptions);
        return Layouts.Vertical().WithChild(new TextNode("  Allow Teams direct messages?").WithForeground(Color.White)).WithChild(list);
    }

    private ILayoutNode BuildInput(string label, string placeholder, string? existing, Action<string?> apply, StepViewCallbacks callbacks, bool isSecret = false, bool allowBlank = false)
    {
        var input = new TextInputNode().WithPlaceholder(placeholder);
        if (isSecret)
            input.AsPassword();
        WizardStepHelpers.SeedTextInput(input, existing);
        input.OnFocused();
        _focusedInput = input;
        _focusedList = null;
        WizardStepHelpers.SyncInputToViewModel(input, StageFocusedInput, callbacks);
        input.Submitted.Subscribe(text =>
        {
            if (!allowBlank && string.IsNullOrWhiteSpace(text))
            {
                callbacks.ShowValidationError("This value is required.");
                return;
            }

            apply(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
            callbacks.ClearStatusMessage();
            callbacks.AdvanceStep();
        }).DisposeWith(callbacks.Subscriptions);
        return Layouts.Vertical().WithChild(new TextNode(label).WithForeground(Color.White)).WithChild(WizardStepHelpers.BuildTextInputPanel(input, placeholder));
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_focusedList is not null)
        {
            _focusedList.HandleInput(key.KeyInfo);
            return true;
        }

        if (_focusedInput is null)
            return false;

        _focusedInput.HandleInput(key.KeyInfo);
        if (key.KeyInfo.Key != ConsoleKey.Enter)
            StageFocusedInput();
        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _focusedInput?.HandlePaste(paste);
        StageFocusedInput();
    }

    public void ClearFocusState()
    {
        _focusedList = null;
        _focusedInput = null;
    }

    private void StageFocusedInput() { }
}
