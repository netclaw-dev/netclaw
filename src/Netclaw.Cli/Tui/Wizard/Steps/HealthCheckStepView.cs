// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the HealthCheck wizard step.
/// Displays running/completed health check results.
/// </summary>
public sealed class HealthCheckStepView : IWizardStepView
{
    public string StepId => WizardStepIds.HealthCheck;
    public bool ManagesOwnFocusState => true;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (HealthCheckStepViewModel)stepVm;
        var items = vm.Results;
        var lines = new List<ILayoutNode>();

        foreach (var item in items)
        {
            var (icon, color) = item.Passed switch
            {
                true => ("\u2713", Color.Green),
                false => ("\u2717", Color.Red),
                null => ("\u25cf", Color.Yellow)
            };
            lines.Add(new TextNode($"  {icon}  {item.Label}").WithForeground(color));
        }

        if (lines.Count == 0)
            lines.Add(new TextNode("  Press Enter to run health checks...").WithForeground(Color.BrightBlack));

        var layout = Layouts.Vertical();
        foreach (var line in lines)
            layout.WithChild(line);
        return layout;
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        // Health check step has no interactive components — input handled by orchestrator page
        return false;
    }

    public void HandlePaste(PasteEvent paste) { }

    public void ClearFocusState() { }
}
