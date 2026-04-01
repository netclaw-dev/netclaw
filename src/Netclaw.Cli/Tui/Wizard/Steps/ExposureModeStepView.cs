using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the ExposureMode wizard step.
/// Sub-step 0: mode selection list (four modes, local pre-selected).
/// Sub-step 1: informational notice (tailscale-serve) or high-risk warning
///             with explicit confirmation (tailscale-funnel, cloudflare-tunnel).
/// </summary>
public sealed class ExposureModeStepView : IWizardStepView
{
    private SelectionListNode<string>? _modeList;
    private SelectionListNode<string>? _confirmList;
    private IFocusable? _lastFocusedList;

    public string StepId => "exposure-mode";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (ExposureModeStepViewModel)stepVm;

        return vm.CurrentSubStep switch
        {
            0 => BuildModeSelection(vm, callbacks),
            1 => BuildConfirmation(vm, callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildModeSelection(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        const string localLabel = "Local \u2014 loopback only, safest (recommended)";
        const string serveLabel = "Tailscale Serve \u2014 accessible within your tailnet";
        const string funnelLabel = "Tailscale Funnel \u2014 public internet \u26a0";
        const string cloudflareLabel = "Cloudflare Tunnel \u2014 public internet \u26a0";

        _modeList = Layouts.SelectionList(localLabel, serveLabel, funnelLabel, cloudflareLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _modeList.OnFocused();
        _lastFocusedList = _modeList;
        _confirmList = null;

        _modeList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var choice = selected[0];
                    vm.SelectedMode = choice switch
                    {
                        serveLabel => ExposureMode.TailscaleServe,
                        funnelLabel => ExposureMode.TailscaleFunnel,
                        cloudflareLabel => ExposureMode.CloudflareTunnel,
                        _ => ExposureMode.Local
                    };
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  How will this Netclaw daemon be accessed?").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(_modeList)
            .WithSpacing(1)
            .WithChild(new TextNode("  \u26a0 = exposes daemon beyond this machine. Ensure auth is configured first.")
                .WithForeground(Color.BrightBlack));
    }

    private ILayoutNode BuildConfirmation(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;

        if (vm.IsHighRisk)
            return BuildHighRiskWarning(vm, callbacks);

        return BuildTailscaleServeNotice(vm, callbacks);
    }

    private ILayoutNode BuildHighRiskWarning(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        var modeLabel = vm.SelectedMode == ExposureMode.TailscaleFunnel
            ? "Tailscale Funnel"
            : "Cloudflare Tunnel";

        _confirmList = Layouts.SelectionList("I understand the risks \u2014 continue")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;

        _confirmList.SelectionConfirmed
            .Subscribe(_ => callbacks.AdvanceStep())
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  \u26a0  {modeLabel} exposes your daemon to the public internet.")
                .WithForeground(Color.Yellow))
            .WithSpacing(1)
            .WithChild(new TextNode("  Before proceeding, ensure:").WithForeground(Color.White))
            .WithChild(new TextNode("    \u2022 Hub authentication is configured (device pairing or bearer token)").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("    \u2022 Your tunnel is running and healthy").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("    \u2022 You trust your security posture selection").WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(_confirmList);
    }

    private ILayoutNode BuildTailscaleServeNotice(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _confirmList = Layouts.SelectionList("Got it \u2014 continue")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;

        _confirmList.SelectionConfirmed
            .Subscribe(_ => callbacks.AdvanceStep())
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Tailscale Serve: daemon accessible within your tailnet only.")
                .WithForeground(Color.Cyan))
            .WithSpacing(1)
            .WithChild(new TextNode("  Devices on your tailnet can reach the daemon. Not reachable from the public internet.")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  Ensure `tailscaled` is running before starting Netclaw.")
                .WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(_confirmList);
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
        _modeList = null;
        _confirmList = null;
    }
}
