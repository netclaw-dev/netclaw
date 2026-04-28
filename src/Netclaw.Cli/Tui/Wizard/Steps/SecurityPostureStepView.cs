// -----------------------------------------------------------------------
// <copyright file="SecurityPostureStepView.cs" company="Petabridge, LLC">
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
/// Termina view for the SecurityPosture wizard step.
/// Displays a selection list: Personal / Team / Public.
/// </summary>
public sealed class SecurityPostureStepView : IWizardStepView
{
    private SelectionListNode<string>? _postureList;
    private IFocusable? _lastFocusedList;

    public string StepId => "security-posture";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (SecurityPostureStepViewModel)stepVm;

        _postureList = Layouts.SelectionList(
                "Personal \u2014 Only you on this machine",
                "Team \u2014 Shared with trusted teammates",
                "Public \u2014 Open to untrusted users")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _postureList.OnFocused();
        _lastFocusedList = _postureList;

        // Wire selection → set VM state → advance wizard
        _postureList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var choice = selected[0];
                    vm.SelectedPosture = choice.StartsWith("Personal", StringComparison.Ordinal)
                        ? DeploymentPosture.Personal
                        : choice.StartsWith("Team", StringComparison.Ordinal)
                            ? DeploymentPosture.Team
                            : DeploymentPosture.Public;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Who will interact with this Netclaw instance?").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(_postureList)
            .WithSpacing(1)
            .WithChild(new TextNode("  Personal = full shell + tools. Team = no shell, shared tools.")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  Public = minimal tools, restricted filesystem.")
                .WithForeground(Color.BrightBlack));
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

    public void HandlePaste(PasteEvent paste)
    {
        // No text inputs in this step
    }

    public void ClearFocusState()
    {
        _lastFocusedList = null;
        _postureList = null;
    }
}
