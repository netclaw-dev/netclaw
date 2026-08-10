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
    private IDisposable? _postureList;
    private IFocusable? _lastFocusedList;

    public string StepId => WizardStepIds.SecurityPosture;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (SecurityPostureStepViewModel)stepVm;

        var postureList = Layouts.SelectionList<PostureOption>(
                [
                    new(DeploymentPosture.Personal),
                    new(DeploymentPosture.Team),
                    new(DeploymentPosture.Public)
                ],
                static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _postureList = postureList;
        postureList.OnFocused();
        _lastFocusedList = postureList;

        postureList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.SelectedPosture = selected[0].Value;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Who will interact with this Netclaw instance?").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(postureList)
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

file record PostureOption(DeploymentPosture Value)
{
    public override string ToString() => Value switch
    {
        DeploymentPosture.Personal => "Personal — Only you on this machine",
        DeploymentPosture.Team => "Team — Shared with trusted teammates",
        DeploymentPosture.Public => "Public — Open to untrusted users",
        _ => Value.ToString()
    };
}
