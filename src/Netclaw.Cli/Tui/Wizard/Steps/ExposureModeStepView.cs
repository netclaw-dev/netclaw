// -----------------------------------------------------------------------
// <copyright file="ExposureModeStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;
using Netclaw.Configuration;
using Netclaw.Cli.Tui.Workflow;
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
    private static readonly IReadOnlyList<SelectionOption<ExposureMode>> ModeOptions =
    [
        new(ExposureMode.Local, "Local — loopback only, safest (recommended)"),
        new(ExposureMode.ReverseProxy, "Reverse Proxy — behind nginx, Caddy, Traefik, IIS, ALB, etc."),
        new(ExposureMode.TailscaleServe, "Tailscale Serve — accessible within your tailnet"),
        new(ExposureMode.TailscaleFunnel, "Tailscale Funnel — public internet ⚠"),
        new(ExposureMode.CloudflareTunnel, "Cloudflare Tunnel — public internet ⚠")
    ];

    private ActiveSelectionList<SelectionOption<ExposureMode>>? _modeList;
    private SelectionListNode<string>? _confirmList;
    private IDisposable? _webhookList;
    private TextInputNode? _hostInput;
    private TextInputNode? _trustedProxiesInput;
    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;

    // Transient validation error messages, set by submit handlers and rendered
    // above the corresponding input on the next layout build. Cleared on every
    // ClearFocusState (i.e. when navigating away from this wizard step).
    private string? _hostInputError;
    private string? _trustedProxiesInputError;

    public string StepId => WizardStepIds.ExposureMode;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        var vm = (ExposureModeStepViewModel)stepVm;

        if (vm.CurrentSubStep != 0)
            _modeList = null;

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

        return BuildConfirmation(vm, callbacks);
    }

    private ILayoutNode BuildModeSelection(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _modeList = null;
        _lastFocusedList = null;
        _lastFocusedInput = null;
        _confirmList = null;
        _webhookList = null;
        _hostInput = null;
        _trustedProxiesInput = null;

        var modeList = new ActiveSelectionList<SelectionOption<ExposureMode>>(
            ModeOptions,
            static option => option.Label,
            option => option.Value == vm.SelectedMode,
            confirmed: option =>
            {
                vm.SelectedMode = option.Value;
                callbacks.AdvanceStep();
            },
            changed: callbacks.RequestRedraw);
        modeList.FocusFirst(option => option.Value == vm.SelectedMode);

        _modeList = modeList;

        return WorkflowViewComponents.BuildSelectionScreen(
            heading: "How will this Netclaw daemon be accessed?",
            selector: modeList.AsLayout(),
            legend: ActiveSelectionList<SelectionOption<ExposureMode>>.BuildLegend("active exposure mode"),
            supportText: "⚠ = exposes daemon beyond this machine. Ensure auth is configured first.",
            supportColor: Color.BrightBlack);
    }

    private ILayoutNode BuildReverseProxyHost(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _hostInput = new TextInputNode().WithPlaceholder(ExposureModeStepViewModel.DefaultReverseProxyHost);
        _hostInput.Text = vm.Host;
        _hostInput.OnFocused();
        _lastFocusedInput = _hostInput;
        _lastFocusedList = null;

        _hostInput.Submitted
            .Subscribe(text =>
            {
                var candidate = string.IsNullOrWhiteSpace(text)
                    ? ExposureModeStepViewModel.DefaultReverseProxyHost
                    : text.Trim();

                // Mirror DaemonExposureValidator.IsLoopbackHost — reject inline so the
                // operator sees the error in the wizard instead of at daemon startup.
                if (DaemonExposureValidator.IsLoopbackHost(candidate))
                {
                    _hostInputError = $"'{candidate}' is loopback — not allowed for reverse-proxy mode. Use a non-loopback bind address (e.g. 0.0.0.0 or this host's internal IP).";
                    callbacks.InvalidateAndRedraw();
                    return;
                }

                _hostInputError = null;
                vm.Host = candidate;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Reverse proxy: bind address").WithForeground(Color.White))
            .WithChild(new TextNode("  Daemon will listen on this address. Loopback (127.0.0.1, ::1, localhost)")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  is not allowed — loopback auto-auth cannot be inherited through a proxy.")
                .WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_hostInput, "Bind address"));

        if (_hostInputError is not null)
            layout = layout.WithChild(new TextNode($"  ✗ {_hostInputError}").WithForeground(Color.Red));

        return layout;
    }

    private ILayoutNode BuildReverseProxyTrustedProxies(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _trustedProxiesInput = new TextInputNode().WithPlaceholder("10.0.0.0/24, 192.168.1.5");
        _trustedProxiesInput.Text = string.Join(", ", vm.TrustedProxies);
        _trustedProxiesInput.OnFocused();
        _lastFocusedInput = _trustedProxiesInput;
        _lastFocusedList = null;

        _trustedProxiesInput.Submitted
            .Subscribe(text =>
            {
                var parsed = WizardStepHelpers.ParseUserIds(text);

                // Empty submit: do NOT overwrite previously captured entries. The yellow
                // help-line below the input already tells the operator what's required;
                // we add an inline error indicator so a no-op Enter is visibly distinct
                // from a successful submit.
                if (parsed.Count == 0)
                {
                    _trustedProxiesInputError = vm.TrustedProxies.Count > 0
                        ? "Empty input ignored — your previous entries are kept. Enter to continue, or type new values to replace them."
                        : "Empty input rejected — enter at least one IP or CIDR.";
                    callbacks.InvalidateAndRedraw();
                    return;
                }

                // Per-entry IP/CIDR validation — same canonical helper the daemon uses
                // at startup (DaemonExposureValidator.TryParseTrustedProxy). The wizard's
                // job is to produce a config the daemon will actually start with; let
                // the operator fix typos here, not after the wizard exits.
                var errors = new List<string>();
                foreach (var entry in parsed)
                {
                    if (!DaemonExposureValidator.TryParseTrustedProxy(entry, out _, out var error))
                        errors.Add(error ?? $"'{entry}' is not a valid IP or CIDR.");
                }

                if (errors.Count > 0)
                {
                    _trustedProxiesInputError = string.Join("  ", errors);
                    callbacks.InvalidateAndRedraw();
                    return;
                }

                _trustedProxiesInputError = null;
                vm.TrustedProxies = parsed;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var helpLine = vm.TrustedProxies.Count == 0
            ? new TextNode("  At least one IP or CIDR is required — the daemon will not start without it.")
                .WithForeground(Color.Yellow)
            : new TextNode($"  {vm.TrustedProxies.Count} trusted proxy entr{(vm.TrustedProxies.Count == 1 ? "y" : "ies")} captured. Press Enter to continue.")
                .WithForeground(Color.BrightBlack);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Reverse proxy: trusted proxies").WithForeground(Color.White))
            .WithChild(new TextNode("  Comma-separated IP addresses or CIDR ranges. Forwarded headers from any")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("  other source will be ignored.")
                .WithForeground(Color.BrightBlack))
            .WithSpacing(1)
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_trustedProxiesInput, "Trusted proxies"))
            .WithChild(helpLine);

        if (_trustedProxiesInputError is not null)
            layout = layout.WithChild(new TextNode($"  ✗ {_trustedProxiesInputError}").WithForeground(Color.Red));

        return layout;
    }

    private ILayoutNode BuildReverseProxyNotice(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        _confirmList = Layouts.SelectionList("Got it — continue")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;
        _lastFocusedInput = null;

        var servingUrl = FormatServingUrl(vm.Host);
        var bindsToAllInterfaces = vm.Host == "0.0.0.0" || vm.Host == "::";
        var proxiesLabel = vm.TrustedProxies.Count == 0
            ? "(none)"
            : string.Join(", ", vm.TrustedProxies);

        _confirmList.SelectionConfirmed
            .Subscribe(_ => callbacks.AdvanceStep())
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Reverse proxy configured").WithForeground(Color.Cyan))
            .WithSpacing(1)
            .WithChild(new TextNode($"  Daemon listen address:    {servingUrl}").WithForeground(Color.White))
            .WithChild(new TextNode($"  Trusted proxies:          {proxiesLabel}").WithForeground(Color.White))
            .WithSpacing(1);

        if (bindsToAllInterfaces)
        {
            layout = layout
                .WithChild(new TextNode($"  {vm.Host} binds all interfaces. In your reverse proxy's upstream config,").WithForeground(Color.BrightBlack))
                .WithChild(new TextNode($"  use a reachable address: this host's loopback if the proxy runs on the").WithForeground(Color.BrightBlack))
                .WithChild(new TextNode($"  same machine, or this host's LAN/internal IP if the proxy is remote.").WithForeground(Color.BrightBlack));
        }
        else
        {
            layout = layout
                .WithChild(new TextNode($"  Point your reverse proxy at {servingUrl} and terminate TLS at").WithForeground(Color.BrightBlack))
                .WithChild(new TextNode("  the proxy. Forwarded headers from any other source IP will be ignored.").WithForeground(Color.BrightBlack));
        }

        return layout
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
        return vm.SelectedMode switch
        {
            ExposureMode.TailscaleFunnel => BuildTailscaleFunnelWarning(callbacks),
            ExposureMode.CloudflareTunnel => BuildCloudflareTunnelWarning(callbacks),
            _ => BuildTailscaleServeNotice(vm, callbacks)
        };
    }

    private ILayoutNode BuildTailscaleFunnelWarning(StepViewCallbacks callbacks)
    {
        return BuildHighRiskWarning(
            "Tailscale Funnel",
            [
                "Hub authentication is configured (device pairing or bearer token)",
                "`tailscaled` is running and Funnel is explicitly enabled for this service",
                "You trust your security posture selection"
            ],
            callbacks);
    }

    private ILayoutNode BuildCloudflareTunnelWarning(StepViewCallbacks callbacks)
    {
        return BuildHighRiskWarning(
            "Cloudflare Tunnel",
            [
                "Hub authentication is configured (device pairing or bearer token)",
                "`cloudflared` is running and Cloudflare Access protects the tunnel",
                "You trust your security posture selection"
            ],
            callbacks);
    }

    private ILayoutNode BuildHighRiskWarning(string modeLabel, IReadOnlyList<string> requirements, StepViewCallbacks callbacks)
    {
        _confirmList = Layouts.SelectionList("I understand the risks — continue")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;
        _lastFocusedInput = null;

        _confirmList.SelectionConfirmed
            .Subscribe(_ => callbacks.AdvanceStep())
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode($"  ⚠  {modeLabel} exposes your daemon to the public internet.")
                .WithForeground(Color.Yellow))
            .WithSpacing(1)
            .WithChild(new TextNode("  Before proceeding, ensure:").WithForeground(Color.White));

        foreach (var requirement in requirements)
            layout = layout.WithChild(new TextNode($"    • {requirement}").WithForeground(Color.BrightBlack));

        return layout.WithSpacing(1).WithChild(_confirmList);
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

        return WorkflowViewComponents.BuildNoticeScreen(
            title: "Tailscale Serve: daemon accessible within your tailnet only.",
            bodyLines:
            [
                "Devices on your tailnet can reach the daemon. Not reachable from the public internet.",
                "Ensure `tailscaled` is running before starting Netclaw."
            ],
            confirmation: _confirmList,
            titleColor: Color.Cyan);
    }

    private ILayoutNode BuildWebhookToggle(ExposureModeStepViewModel vm, StepViewCallbacks callbacks)
    {
        var disableOption = new SelectionOption<bool>(false, "No — do not accept inbound webhooks (default)");
        var enableOption = new SelectionOption<bool>(true, "Yes — accept inbound webhook requests");

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

        return WorkflowViewComponents.BuildSelectionScreen(
            heading: "Should this daemon accept inbound webhooks?",
            selector: webhookList,
            supportText: "Inbound webhooks let external services trigger autonomous runs via HTTP POST.\nThis is separate from outbound notification webhooks.",
            supportColor: Color.BrightBlack);
    }

    private static string FormatServingUrl(string host)
    {
        // Bracket-wrap raw IPv6 addresses for URL syntax. Use IPAddress.TryParse rather
        // than a colon-substring check so a typo like 'hostname:port' isn't treated as IPv6.
        var displayHost = host;
        if (!host.StartsWith('[')
            && IPAddress.TryParse(host, out var parsed)
            && parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            displayHost = $"[{host}]";
        }
        return $"http://{displayHost}:{DaemonConfig.DefaultPort}";
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_modeList is not null && _modeList.HandleInput(key.KeyInfo))
            return true;

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
        _hostInputError = null;
        _trustedProxiesInputError = null;
    }
}
