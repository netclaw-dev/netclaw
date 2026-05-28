// -----------------------------------------------------------------------
// <copyright file="HealthCheckStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Netclaw.Cli.Tui;
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
        // Snapshot: Results is mutated off the UI thread by the async health-check and its timer.
        var items = vm.ResultsSnapshot();
        var lines = new List<ILayoutNode>();

        foreach (var item in items)
        {
            if (item.Passed is null)
            {
                lines.Add(SpinnerViews.Labeled(item.Label, Color.Yellow));
                continue;
            }

            var (icon, color) = (item.Passed.Value, item.IsWarning) switch
            {
                (true, true) => ("\u26a0", Color.Yellow),
                (true, _) => ("\u2713", Color.Green),
                (false, _) => ("\u2717", Color.Red),
            };
            lines.Add(new TextNode($"  {icon}  {item.Label}").WithForeground(color));
        }

        if (lines.Count == 0)
            lines.Add(vm.IsRunning.Value
                ? SpinnerViews.Labeled("Starting health checks...", Color.Yellow)
                : new TextNode("  Health checks start automatically...").WithForeground(Color.BrightBlack));

        // Post-flight summary: once the checks finish, nudge toward the bootstrap-vs-config
        // split so the operator knows where ongoing settings live (simplify-netclaw-init §6).
        if (vm.IsComplete.Value)
        {
            lines.Add(new TextNode(""));
            lines.Add(new TextNode("  Next steps:").WithForeground(Color.Gray));
            lines.Add(new TextNode("    netclaw chat    — start talking to your agent").WithForeground(Color.Gray));
            lines.Add(new TextNode("    netclaw config  — adjust settings any time").WithForeground(Color.Gray));
        }

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
