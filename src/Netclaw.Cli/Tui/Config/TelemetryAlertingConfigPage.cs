// -----------------------------------------------------------------------
// <copyright file="TelemetryAlertingConfigPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
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
    private DynamicLayoutNode? _keyBindingsNode;
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

        ViewModel.TelemetryEnabled.Subscribe(_ => InvalidateAll()).DisposeWith(Subscriptions);
        ViewModel.OtlpEndpointDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Webhooks.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Screen.Subscribe(_ => InvalidateAll()).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.FormFieldIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.WebhookNameDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.WebhookUrlDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.WebhookAuthHeaderDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
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
        _contentNode = new DynamicLayoutNode(() => ViewModel.Screen.Value == TelemetryConfigScreen.WebhookForm
            ? BuildWebhookForm()
            : BuildList());

        return _contentNode;
    }

    private ILayoutNode BuildList()
    {
        var webhooks = ViewModel.Webhooks.Value;
        var layout = Layouts.Vertical()
            .WithChild(Header("  Telemetry & Alerting"))
            .WithChild(Hint("  Configure OpenTelemetry export and outbound alert webhooks."))
            .WithChild(Hint("  Slack URLs use Slack format automatically. Delivery-policy tuning is parked."))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Row(0, $"Telemetry enabled          [{Check(ViewModel.TelemetryEnabled.Value)}]"))
            .WithChild(Row(1, $"OTLP endpoint              {ViewModel.OtlpEndpointDraft.Value}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(new TextNode("  Outbound Webhooks").WithForeground(Color.White).Bold());

        if (webhooks.Count == 0)
            layout = layout.WithChild(Hint("  No outbound webhooks configured yet."));

        for (var i = 0; i < webhooks.Count; i++)
        {
            var row = webhooks[i];
            var rowIndex = TelemetryAlertingConfigViewModel.OtlpRowCount + i;
            var auth = row.HasAuthHeader ? "auth" : "—";
            layout = layout.WithChild(Row(
                rowIndex,
                $"{row.Name,-16} {Truncate(row.Url, 40),-40} {row.Format,-8} {auth}"));
        }

        layout = layout.WithChild(Row(ViewModel.AddRowIndex, "+ Add webhook"));

        return layout
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  {FocusedHelp()}"));
    }

    private ILayoutNode BuildWebhookForm()
    {
        var format = ViewModel.DraftFormat;
        var authState = ViewModel.EditingHasPersistedAuthHeader.Value && string.IsNullOrWhiteSpace(ViewModel.WebhookAuthHeaderDraft.Value)
            ? "(stored header preserved — enter - to clear)"
            : string.IsNullOrWhiteSpace(ViewModel.WebhookAuthHeaderDraft.Value) ? "(optional)" : "(new header entered)";

        var title = ViewModel.EditingHasPersistedAuthHeader.Value || !string.IsNullOrWhiteSpace(ViewModel.WebhookNameDraft.Value)
            ? $"  Edit webhook: {DisplayName()}"
            : "  Add outbound webhook";

        return Layouts.Vertical()
            .WithChild(new TextNode(title).WithForeground(Color.White).Bold())
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(FormRow(0, "Name        ", ViewModel.WebhookNameDraft.Value, "(optional)", masked: false))
            .WithChild(FormRow(1, "URL         ", ViewModel.WebhookUrlDraft.Value, "e.g. https://hooks.slack.com/services/…", masked: false))
            .WithChild(FormRow(2, "Auth header ", ViewModel.WebhookAuthHeaderDraft.Value, authState, masked: true))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  Format:  {format} (auto-detected from URL)"))
            .WithChild(Hint("  URL is required. Auth header is optional and stored masked."));
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone)))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
    {
        _keyBindingsNode = new DynamicLayoutNode(() => NetclawTuiChrome.BuildKeyHintLine(
            ViewModel.Screen.Value == TelemetryConfigScreen.WebhookForm
                ? " [↑/↓ or Tab] Fields  [Type/Paste] Edit  [Enter] Save  [Esc] Back  [Ctrl+Q] Quit"
                : " [↑/↓] Navigate  [Space] Toggle  [Enter] Edit/Add/Save  [Delete] Remove  [Type/Paste] Edit  [Esc] Settings Areas  [Ctrl+Q] Quit"));

        return _keyBindingsNode.Height(1);
    }

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

        if (ViewModel.Screen.Value == TelemetryConfigScreen.WebhookForm)
        {
            HandleFormKey(keyInfo);
            return;
        }

        HandleListKey(keyInfo);
    }

    private void HandleListKey(ConsoleKeyInfo keyInfo)
    {
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
                ViewModel.ActivateSelected();
                return;
            case ConsoleKey.Delete:
                ViewModel.RemoveSelectedWebhook();
                return;
            case ConsoleKey.Backspace:
                ViewModel.Backspace();
                return;
        }

        if (!char.IsControl(keyInfo.KeyChar))
            ViewModel.AppendText(keyInfo.KeyChar.ToString());
    }

    private void HandleFormKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveSelection(-1);
                return;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                ViewModel.MoveSelection(1);
                return;
            case ConsoleKey.Enter:
                ViewModel.ActivateSelected();
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

    private ILayoutNode Row(int index, string label)
        => ConfigSelectionRow.Create($"  {label}", index == ViewModel.SelectedRow.Value);

    private ILayoutNode FormRow(int index, string label, string draft, string placeholder, bool masked)
    {
        var isPlaceholder = string.IsNullOrEmpty(draft);
        var value = DisplayField(draft, placeholder, masked);
        // Placeholder/example text renders dim (hint gray) so it never reads as an
        // entered value; a real (or masked) value renders bright white.
        var valueColor = isPlaceholder ? Color.Gray : Color.White;
        return ConfigSelectionRow.CreateLabeled($"  {label} ", value, index == ViewModel.FormFieldIndex.Value, valueColor);
    }

    private string FocusedHelp()
    {
        var row = ViewModel.SelectedRow.Value;
        if (row == 0)
            return "Toggle daemon OTLP logs and metrics export.";
        if (row == 1)
            return "gRPC OTLP collector endpoint, usually port 4317.";
        if (row == ViewModel.AddRowIndex)
            return "Add a new outbound alert target.";
        if (ViewModel.IsWebhookRow(row))
        {
            var webhook = ViewModel.Webhooks.Value[ViewModel.WebhookIndexFor(row)];
            return $"{webhook.Format} format · {(webhook.HasAuthHeader ? "auth header set" : "no auth header")} · Enter to edit, Delete to remove.";
        }

        return string.Empty;
    }

    private string DisplayName()
        => string.IsNullOrWhiteSpace(ViewModel.WebhookNameDraft.Value) ? "(unnamed)" : ViewModel.WebhookNameDraft.Value;

    private static string DisplayField(string value, string placeholder, bool masked)
    {
        if (string.IsNullOrEmpty(value))
            return placeholder;
        return masked ? new string('•', Math.Min(value.Length, 24)) : value;
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : string.Concat(value.AsSpan(0, Math.Max(0, width - 1)), "…");

    private static string Check(bool value) => value ? "x" : " ";
    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.Gray);

    private void InvalidateAll()
    {
        _contentNode?.Invalidate();
        _keyBindingsNode?.Invalidate();
    }

    private static Color ToColor(ConfigStatusTone tone)
        => tone switch
        {
            ConfigStatusTone.Success => Color.Green,
            ConfigStatusTone.Warning => Color.Yellow,
            ConfigStatusTone.Error => Color.Red,
            _ => Color.Gray
        };
}
