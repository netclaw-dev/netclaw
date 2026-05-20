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
/// Termina view for the ExposureMode wizard step. Sub-step layout depends on the
/// selected mode — see <see cref="ExposureModeStepViewModel"/> summary.
///
/// Reverse-proxy adds three sub-steps: bind-address input, trusted-proxies input,
/// and an informational notice that echoes the resulting serving URL.
/// </summary>
public sealed class ExposureModeStepView : IWizardStepView
{
    /// <summary>Default daemon port when the operator does not override it via netclaw.json.</summary>
    private const int DefaultDaemonPort = 5199;

    private IDisposable? _modeList;
    private SelectionListNode<string>? _confirmList;
    private IDisposable? _webhookList;
    private TextInputNode? _hostInput;
    private TextInputNode? _trustedProxiesInput;
    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;

    public string StepId => WizardStepIds.ExposureMode;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (ExposureModeStepViewModel)stepVm;

        if (vm.CurrentSubStep == 0)
            return BuildModeSelection(vm, callbacks);

        if (vm.IsReverseProxy && vm.CurrentSubStep == vm.ReverseProxyHostSubStep)
            return BuildReverseProxyHost(vm, callbacks);

        if (vm.IsReverseProxy && vm.CurrentSubStep == vm.ReverseProxyTrustedProxiesSubStep)
            return BuildReverseProxyTrustedProxies(vm, callbacks);

        if (vm.IsReverseProxy && vm.CurrentSubStep == vm.NoticeSubStep)
            return BuildReverseProxyNotice(vm, callbacks);

        if (vm.CurrentSubStep == vm.WebhookSubStep)
            return BuildWebhookToggle(vm, callbacks);

        // Sub-step 1 confirmation (non-reverse-proxy non-Local modes only)
        return BuildConfirmation(vm, callbacks);
    }

    private ILayoutNode BuildModeSelection(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        var localOption = new SelectionOption<ExposureMode>(ExposureMode.Local,
            "Local — loopback only, safest (recommended)");
        var reverseProxyOption = new SelectionOption<ExposureMode>(ExposureMode.ReverseProxy,
            "Reverse Proxy — behind nginx, Caddy, Traefik, IIS, ALB, etc.");
        var serveOption = new SelectionOption<ExposureMode>(ExposureMode.TailscaleServe,
            "Tailscale Serve — accessible within your tailnet");
        var funnelOption = new SelectionOption<ExposureMode>(ExposureMode.TailscaleFunnel,
            "Tailscale Funnel — public internet ⚠");
        var cloudflareOption = new SelectionOption<ExposureMode>(ExposureMode.CloudflareTunnel,
            "Cloudflare Tunnel — public internet ⚠");

        var modeList = Layouts.SelectionList<SelectionOption<ExposureMode>>(
                [localOption, reverseProxyOption, serveOption, funnelOption, cloudflareOption],
                static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _modeList = modeList;
        modeList.OnFocused();
        _lastFocusedList = modeList;
        _lastFocusedInput = null;
        _confirmList = null;
        _webhookList = null;
        _hostInput = null;
        _trustedProxiesInput = null;

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

    private ILayoutNode BuildReverseProxyHost(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;
        _confirmList = null;
        _webhookList = null;
        _trustedProxiesInput = null;

        _hostInput = new TextInputNode().WithPlaceholder(ExposureModeStepViewModel.DefaultReverseProxyHost);
        _hostInput.Text = vm.Host;
        _hostInput.OnFocused();
        _lastFocusedInput = _hostInput;
        _lastFocusedList = null;

        _hostInput.Submitted
            .Subscribe(text =>
            {
                vm.Host = string.IsNullOrWhiteSpace(text)
                    ? ExposureModeStepViewModel.DefaultReverseProxyHost
                    : text.Trim();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Reverse proxy: bind address").WithForeground(Color.White))
            .WithChild(new TextNode("  Daemon will listen on this address. Loopback (127.0.0.1, ::1) is not allowed —")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  loopback auto-auth cannot be inherited through a proxy.")
                .WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_hostInput, "Bind address"));
    }

    private ILayoutNode BuildReverseProxyTrustedProxies(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;
        _confirmList = null;
        _webhookList = null;
        _hostInput = null;

        _trustedProxiesInput = new TextInputNode().WithPlaceholder("10.0.0.0/24, 192.168.1.5");
        _trustedProxiesInput.Text = string.Join(", ", vm.TrustedProxies);
        _trustedProxiesInput.OnFocused();
        _lastFocusedInput = _trustedProxiesInput;
        _lastFocusedList = null;

        _trustedProxiesInput.Submitted
            .Subscribe(text =>
            {
                var parsed = WizardStepHelpers.ParseUserIds(text);
                vm.TrustedProxies = parsed;

                // The ViewModel gate also blocks advance on empty input, but we redraw here
                // so the help line below the input reflects the latest state immediately.
                if (parsed.Count == 0)
                {
                    callbacks.InvalidateAndRedraw();
                    return;
                }
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var helpLine = vm.TrustedProxies.Count == 0
            ? new TextNode("  At least one IP or CIDR is required — the daemon will not start without it.")
                .WithForeground(Color.Yellow)
            : new TextNode($"  {vm.TrustedProxies.Count} trusted proxy entr{(vm.TrustedProxies.Count == 1 ? "y" : "ies")} captured. Press Enter again to continue.")
                .WithForeground(Color.BrightBlack);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Reverse proxy: trusted proxies").WithForeground(Color.White))
            .WithChild(new TextNode("  Comma-separated IP addresses or CIDR ranges. Forwarded headers from any")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  other source will be ignored.")
                .WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_trustedProxiesInput, "Trusted proxies"))
            .WithChild(helpLine);
    }

    private ILayoutNode BuildReverseProxyNotice(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;
        _webhookList = null;
        _hostInput = null;
        _trustedProxiesInput = null;

        _confirmList = Layouts.SelectionList("Got it — continue")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;
        _lastFocusedInput = null;

        _confirmList.SelectionConfirmed
            .Subscribe(_ => callbacks.AdvanceStep())
            .DisposeWith(callbacks.Subscriptions);

        var servingUrl = FormatServingUrl(vm.Host);
        var proxiesLabel = vm.TrustedProxies.Count == 0
            ? "(none)"
            : string.Join(", ", vm.TrustedProxies);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Reverse proxy configured").WithForeground(Color.Cyan))
            .WithSpacing(1)
            .WithChild(new TextNode($"  Daemon will listen on:    {servingUrl}").WithForeground(Color.White))
            .WithChild(new TextNode($"  Trusted proxies:          {proxiesLabel}").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(new TextNode($"  Point your reverse proxy at {servingUrl} and terminate TLS at").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  the proxy. Forwarded headers from any other source IP will be ignored.").WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(new TextNode("  You are responsible for:").WithForeground(Color.White))
            .WithChild(new TextNode("    • Terminating TLS at the proxy").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("    • Restricting inbound access at the proxy / firewall").WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("    • Setting X-Forwarded-For and X-Forwarded-Proto correctly").WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(_confirmList);
    }

    private ILayoutNode BuildConfirmation(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;
        _webhookList = null;
        _hostInput = null;
        _trustedProxiesInput = null;

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
        _lastFocusedInput = null;

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
        _lastFocusedInput = null;

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
        _hostInput = null;
        _trustedProxiesInput = null;

        var webhookList = Layouts.SelectionList<SelectionOption<bool>>(
                [disableOption, enableOption], static o => o.ToString())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _webhookList = webhookList;
        webhookList.OnFocused();
        _lastFocusedList = webhookList;
        _lastFocusedInput = null;

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

    private static string FormatServingUrl(string host)
    {
        var displayHost = host;
        if (host.Contains(':') && !host.StartsWith('['))
            displayHost = $"[{host}]";
        return $"http://{displayHost}:{DefaultDaemonPort}";
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
        _modeList = null;
        _confirmList = null;
        _webhookList = null;
        _hostInput = null;
        _trustedProxiesInput = null;
    }
}
