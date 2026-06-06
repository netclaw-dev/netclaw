// -----------------------------------------------------------------------
// <copyright file="TelemetryAlertingConfigPage.cs" company="Petabridge, LLC">
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

namespace Netclaw.Cli.Tui.Config;

internal sealed class TelemetryAlertingConfigPage : ReactivePage<TelemetryAlertingConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;
    private readonly TextInputNode _pasteBuffer = new();

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        ViewModel.TelemetryEnabled.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.OtlpEndpointDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.OutboundWebhookCount.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.OutboundWebhookUrlDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.OutboundWebhookAuthHeaderDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.HasPersistedWebhookAuthHeader.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Telemetry & Alerting", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            var authState = ViewModel.HasPersistedWebhookAuthHeader.Value && string.IsNullOrWhiteSpace(ViewModel.OutboundWebhookAuthHeaderDraft.Value)
                ? "(stored header preserved)"
                : string.IsNullOrWhiteSpace(ViewModel.OutboundWebhookAuthHeaderDraft.Value) ? "(optional)" : "(new header entered)";

            return Layouts.Vertical()
                .WithChild(Header("  Telemetry & Alerting"))
                .WithChild(Hint("  Configure OpenTelemetry export and operational outbound webhooks."))
                .WithChild(Hint("  Delivery-policy tuning is intentionally parked for a later pass."))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Hint($"  Current: telemetry={(ViewModel.TelemetryEnabled.Value ? "enabled" : "disabled")}, outbound webhooks={ViewModel.OutboundWebhookCount.Value}"))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Row(0,
                    $"Telemetry enabled          [{Check(ViewModel.TelemetryEnabled.Value)}]",
                    "Toggle daemon OTLP logs and metrics export."))
                .WithChild(Row(1,
                    $"OTLP endpoint              {ViewModel.OtlpEndpointDraft.Value}",
                    "gRPC OTLP collector endpoint, usually port 4317."))
                .WithChild(Row(2,
                    $"Outbound webhook URL       {DisplayDraft(ViewModel.OutboundWebhookUrlDraft.Value)}",
                    "Operational alert target; Slack URLs get Slack format automatically."))
                .WithChild(Row(3,
                    $"Outbound auth header       {authState}",
                    "Optional 'Header-Name: value'; leave blank to preserve stored headers."));
        });

        return _contentNode;
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone)))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
        => NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Space] Toggle/Save  [Type/Paste] Edit  [Backspace] Delete  [Enter] Apply  [Esc] Settings Areas  [Ctrl+Q] Quit");

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.GoBack();
            return;
        }

        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveSelection(-1);
                return;
            case ConsoleKey.DownArrow:
                ViewModel.MoveSelection(1);
                return;
            case ConsoleKey.Spacebar when ViewModel.SelectedRow.Value == 0:
                ViewModel.ToggleTelemetry();
                return;
            case ConsoleKey.Enter:
                if (ViewModel.SelectedRow.Value == 0)
                    ViewModel.ActivateSelected();
                else
                    ViewModel.Save();
                return;
            case ConsoleKey.Backspace:
                ViewModel.Backspace();
                return;
        }

        if (!char.IsControl(keyInfo.KeyChar))
            ViewModel.AppendText(keyInfo.KeyChar.ToString());
    }

    private void HandlePaste(PasteEvent paste)
    {
        _pasteBuffer.Text = string.Empty;
        _pasteBuffer.HandlePaste(paste);
        ViewModel.AppendText(_pasteBuffer.Text);
    }

    private ILayoutNode Row(int index, string label, string description)
    {
        var focused = index == ViewModel.SelectedRow.Value;
        var prefix = focused ? "> " : "  ";
        var color = focused ? Color.Cyan : Color.White;
        return Text($"  {prefix}{label,-58} {description}", color);
    }

    private static string Check(bool value) => value ? "x" : " ";
    private static string DisplayDraft(string value) => string.IsNullOrWhiteSpace(value) ? "(leave unchanged)" : value;
    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.Gray);
    private static TextNode Text(string text, Color color) => new TextNode(text).WithForeground(color);

    private static Color ToColor(ConfigStatusTone tone)
        => tone switch
        {
            ConfigStatusTone.Success => Color.Green,
            ConfigStatusTone.Warning => Color.Yellow,
            ConfigStatusTone.Error => Color.Red,
            _ => Color.Gray
        };
}
