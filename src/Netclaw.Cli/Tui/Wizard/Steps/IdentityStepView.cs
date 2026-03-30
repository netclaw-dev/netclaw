using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Identity wizard step.
/// 6 sub-steps: agent name → comm style → user name → timezone → workspaces directory → webhook URL.
/// </summary>
public sealed class IdentityStepView : IWizardStepView
{
    private TextInputNode? _agentNameInput;
    private SelectionListNode<string>? _commStyleList;
    private TextInputNode? _userNameInput;
    private TextInputNode? _timezoneInput;
    private TextInputNode? _workspacesInput;
    private TextInputNode? _webhookUrlInput;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;

    public string StepId => "identity";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (IdentityStepViewModel)stepVm;

        return vm.CurrentSubStep switch
        {
            0 => BuildAgentName(vm, callbacks),
            1 => BuildCommStyle(vm, callbacks),
            2 => BuildUserName(vm, callbacks),
            3 => BuildTimezone(vm, callbacks),
            4 => BuildWorkspacesDirectory(vm, callbacks),
            5 => BuildWebhookUrl(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildAgentName(IdentityStepViewModel vm, StepViewCallbacks callbacks)
    {
        _agentNameInput = new TextInputNode().WithPlaceholder("Netclaw");
        _agentNameInput.Text = vm.AgentName;
        _agentNameInput.OnFocused();
        _lastFocusedInput = _agentNameInput;
        _lastFocusedList = null;

        _agentNameInput.Submitted
            .Subscribe(text =>
            {
                vm.AgentName = string.IsNullOrWhiteSpace(text) ? "Netclaw" : text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Agent name:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Name")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_agentNameInput)
                .Height(3));
    }

    private ILayoutNode BuildCommStyle(IdentityStepViewModel vm, StepViewCallbacks callbacks)
    {
        _commStyleList = Layouts.SelectionList(
                "Concise & casual",
                "Concise & formal",
                "Detailed & casual",
                "Detailed & formal")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _commStyleList.OnFocused();
        _lastFocusedList = _commStyleList;
        _lastFocusedInput = null;

        _commStyleList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.CommunicationStyle = selected[0];
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Communication style:").WithForeground(Color.White))
            .WithChild(_commStyleList);
    }

    private ILayoutNode BuildUserName(IdentityStepViewModel vm, StepViewCallbacks callbacks)
    {
        _userNameInput = new TextInputNode().WithPlaceholder("Your name");
        if (!string.IsNullOrWhiteSpace(vm.UserName))
            _userNameInput.Text = vm.UserName;

        _userNameInput.OnFocused();
        _lastFocusedInput = _userNameInput;
        _lastFocusedList = null;

        _userNameInput.Submitted
            .Subscribe(text =>
            {
                vm.UserName = string.IsNullOrWhiteSpace(text) ? null : text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Your name:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Name")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_userNameInput)
                .Height(3));
    }

    private ILayoutNode BuildTimezone(IdentityStepViewModel vm, StepViewCallbacks callbacks)
    {
        _timezoneInput = new TextInputNode().WithPlaceholder(TimeZoneInfo.Local.Id);
        _timezoneInput.Text = vm.UserTimezone;

        _timezoneInput.OnFocused();
        _lastFocusedInput = _timezoneInput;
        _lastFocusedList = null;

        _timezoneInput.Submitted
            .Subscribe(text =>
            {
                vm.UserTimezone = string.IsNullOrWhiteSpace(text) ? TimeZoneInfo.Local.Id : text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Your timezone:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Timezone")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_timezoneInput)
                .Height(3));
    }

    private ILayoutNode BuildWorkspacesDirectory(IdentityStepViewModel vm, StepViewCallbacks callbacks)
    {
        _workspacesInput = new TextInputNode().WithPlaceholder(vm.WorkspacesDirectory);
        _workspacesInput.Text = vm.WorkspacesDirectory;

        _workspacesInput.OnFocused();
        _lastFocusedInput = _workspacesInput;
        _lastFocusedList = null;

        _workspacesInput.Submitted
            .Subscribe(text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                    vm.WorkspacesDirectory = text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Projects directory:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Workspaces")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_workspacesInput)
                .Height(3));
    }

    private ILayoutNode BuildWebhookUrl(IdentityStepViewModel vm, StepViewCallbacks callbacks)
    {
        _webhookUrlInput = new TextInputNode()
            .WithPlaceholder("https://hooks.slack.com/services/...");

        if (!string.IsNullOrWhiteSpace(vm.WebhookUrl))
            _webhookUrlInput.Text = vm.WebhookUrl;

        _webhookUrlInput.OnFocused();
        _lastFocusedInput = _webhookUrlInput;
        _lastFocusedList = null;

        _webhookUrlInput.Submitted
            .Subscribe(text =>
            {
                vm.WebhookUrl = string.IsNullOrWhiteSpace(text) ? null : text;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Notification webhook URL (optional, press Enter to skip):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Webhook")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_webhookUrlInput)
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
        _agentNameInput = null;
        _commStyleList = null;
        _userNameInput = null;
        _timezoneInput = null;
        _workspacesInput = null;
        _webhookUrlInput = null;
    }
}
