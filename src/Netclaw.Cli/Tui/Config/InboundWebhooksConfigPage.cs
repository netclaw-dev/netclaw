// -----------------------------------------------------------------------
// <copyright file="InboundWebhooksConfigPage.cs" company="Petabridge, LLC">
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

internal sealed class InboundWebhooksConfigPage : ReactivePage<InboundWebhooksConfigViewModel>
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

        ViewModel.Enabled.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.TimeoutDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.RouteSummary.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Inbound Webhooks", BuildInnerLayout());

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
            var routes = ViewModel.RouteSummary.Value;
            var layout = Layouts.Vertical()
                .WithChild(Header("  Inbound Webhooks"))
                .WithChild(Hint("  Global webhook enablement lives here. Route files stay owned by `netclaw webhooks`."))
                .WithChild(Layouts.Empty().Height(1));

            layout = layout.WithChild(Row(0,
                $"Enabled                 [{Check(ViewModel.Enabled.Value)}]",
                "Toggle global webhook endpoint registration."));
            layout = layout.WithChild(Row(1,
                $"Execution timeout       {ViewModel.TimeoutDraft.Value} seconds",
                "Maximum autonomous webhook run time before failure."));
            layout = layout.WithChild(Row(2,
                "Route authoring          netclaw webhooks",
                "Use `netclaw webhooks set|list|validate`; this editor never creates dummy routes."));

            layout = layout
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Hint($"  Routes: total={routes.Total}, enabled={routes.Enabled}, disabled={routes.Disabled}, invalid={routes.Invalid}"));

            if (ViewModel.Enabled.Value && routes.Enabled == 0)
            {
                layout = layout.WithChild(Text(
                    "  Diagnostic: enabled with no valid routes will fail closed. Add a route before saving enabled state.",
                    Color.Yellow));
            }

            return layout;
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
        => NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Space] Toggle/Save  [Type] Edit timeout  [Enter] Apply  [Esc] Settings Areas  [Ctrl+Q] Quit");

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
                ViewModel.ToggleEnabled();
                return;
            case ConsoleKey.Enter:
                ViewModel.Save();
                return;
            case ConsoleKey.Backspace:
                ViewModel.BackspaceTimeout();
                return;
        }

        if (!char.IsControl(keyInfo.KeyChar))
            ViewModel.AppendTimeoutText(keyInfo.KeyChar.ToString());
    }

    private void HandlePaste(PasteEvent paste)
    {
        _pasteBuffer.Text = string.Empty;
        _pasteBuffer.HandlePaste(paste);
        ViewModel.AppendTimeoutText(_pasteBuffer.Text);
    }

    private ILayoutNode Row(int index, string label, string description)
    {
        var focused = index == ViewModel.SelectedRow.Value;
        var prefix = focused ? "> " : "  ";
        var color = focused ? Color.Cyan : Color.White;
        return Text($"  {prefix}{label,-40} {description}", color);
    }

    private static string Check(bool value) => value ? "x" : " ";
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
