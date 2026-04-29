// -----------------------------------------------------------------------
// <copyright file="ExposureModeStepView.cs" company="Petabridge, LLC">
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
/// Termina view for the ExposureMode wizard step.
/// Sub-step 0: mode selection list (four modes, local pre-selected).
/// Sub-step 1 (non-local only): informational notice (tailscale-serve) or high-risk warning
///             with explicit confirmation (tailscale-funnel, cloudflare-tunnel).
/// Last sub-step: inbound webhook enable/disable toggle.
/// </summary>
public sealed class ExposureModeStepView : IWizardStepView
{
    private IDisposable? _modeList;
    private SelectionListNode<string>? _confirmList;
    private IDisposable? _webhookList;
    private IFocusable? _lastFocusedList;

    public string StepId => WizardStepIds.ExposureMode;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (ExposureModeStepViewModel)stepVm;

        if (vm.CurrentSubStep == 0)
            return BuildModeSelection(vm, callbacks);

        if (vm.CurrentSubStep == vm.WebhookSubStep)
            return BuildWebhookToggle(vm, callbacks);

        // Sub-step 1 confirmation (non-Local modes only)
        return BuildConfirmation(vm, callbacks);
    }

    private ILayoutNode BuildModeSelection(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        var localOption = new SelectionOption<ExposureMode>(ExposureMode.Local,
            "Local — loopback only, safest (recommended)");
        var serveOption = new SelectionOption<ExposureMode>(ExposureMode.TailscaleServe,
            "Tailscale Serve — accessible within your tailnet");
        var funnelOption = new SelectionOption<ExposureMode>(ExposureMode.TailscaleFunnel,
            "Tailscale Funnel — public internet ⚠");
        var cloudflareOption = new SelectionOption<ExposureMode>(ExposureMode.CloudflareTunnel,
            "Cloudflare Tunnel — public internet ⚠");

        var modeList = Layouts.SelectionList<SelectionOption<ExposureMode>>(
                [localOption, serveOption, funnelOption, cloudflareOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _modeList = modeList;
        modeList.OnFocused();
        _lastFocusedList = modeList;
        _confirmList = null;
        _webhookList = null;

        modeList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.SelectedMode = selected[0].Value;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  How will this Netclaw daemon be accessed?").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(modeList)
            .WithSpacing(1)
            .WithChild(new TextNode("  ⚠ = exposes daemon beyond this machine. Ensure auth is configured first.")
                .WithForeground(Color.BrightBlack));
    }

    private ILayoutNode BuildConfirmation(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;
        _webhookList = null;

        if (vm.IsHighRisk)
            return BuildHighRiskWarning(vm, callbacks);

        return BuildTailscaleServeNotice(vm, callbacks);
    }

    private ILayoutNode BuildHighRiskWarning(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        var modeLabel = vm.SelectedMode == ExposureMode.TailscaleFunnel
            ? "Tailscale Funnel"
            : "Cloudflare Tunnel";

        _confirmList = Layouts.SelectionList("I understand the risks — continue")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;

        _confirmList.SelectionConfirmed
            .Subscribe(_ => callbacks.AdvanceStep())
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  ⚠  {modeLabel} exposes your daemon to the public internet.")
                .WithForeground(Color.Yellow))
            .WithSpacing(1)
            .WithChild(new TextNode("  Before proceeding, ensure:").WithForeground(Color.White))
            .WithChild(new TextNode("    • Hub authentication is configured (device pairing or bearer token)").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("    • Your tunnel is running and healthy").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("    • You trust your security posture selection").WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(_confirmList);
    }

    private ILayoutNode BuildTailscaleServeNotice(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _confirmList = Layouts.SelectionList("Got it — continue")
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

    private ILayoutNode BuildWebhookToggle(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        var disableOption = new SelectionOption<bool>(false, "No — do not accept inbound webhooks (default)");
        var enableOption = new SelectionOption<bool>(true, "Yes — accept inbound webhook requests");

        _modeList = null;
        _confirmList = null;

        var webhookList = Layouts.SelectionList<SelectionOption<bool>>(
                [disableOption, enableOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _webhookList = webhookList;
        webhookList.OnFocused();
        _lastFocusedList = webhookList;

        webhookList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    vm.WebhooksEnabled = selected[0].Value;
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Should this daemon accept inbound webhooks?").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(webhookList)
            .WithSpacing(1)
            .WithChild(new TextNode("  Inbound webhooks let external services trigger autonomous runs via HTTP POST.")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  This is separate from outbound notification webhooks.")
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
        _modeList = null;
        _confirmList = null;
        _webhookList = null;
    }
}
