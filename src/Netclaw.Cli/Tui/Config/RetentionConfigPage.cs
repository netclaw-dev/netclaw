// -----------------------------------------------------------------------
// <copyright file="RetentionConfigPage.cs" company="Petabridge, LLC">
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

internal sealed class RetentionConfigPage : ReactivePage<RetentionConfigViewModel>
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

        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        // A save changes the saved value without changing the typed text, so redraw on any status change.
        ViewModel.Status.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        foreach (var row in ViewModel.Rows)
            row.Draft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Data Retention", BuildInnerLayout());

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
            var layout = Layouts.Vertical()
                .WithChild(Header("  Data Retention"))
                .WithChild(Hint("  How long the daemon keeps data before it deletes it. 0 keeps the data forever."))
                .WithChild(Layouts.Empty().Height(1));

            for (var i = 0; i < ViewModel.Rows.Count; i++)
            {
                var row = ViewModel.Rows[i];
                layout = layout.WithChild(ConfigSelectionRow.Create(
                    $"  {row.Setting.Label,-28} {ViewModel.DisplayValue(row)}",
                    i == ViewModel.SelectedRow.Value));
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
        => NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Type/Paste] Edit days  [Enter] Save  [Esc] Settings Areas  [Ctrl+Q] Quit");

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        switch (keyInfo.Key)
        {
            case ConsoleKey.Escape:
                ViewModel.GoBack();
                return;
            case ConsoleKey.UpArrow:
                ViewModel.MoveSelection(-1);
                return;
            case ConsoleKey.DownArrow:
                ViewModel.MoveSelection(1);
                return;
            case ConsoleKey.Enter:
                ViewModel.Save();
                return;
            case ConsoleKey.Backspace:
                ViewModel.Backspace();
                return;
        }

        if (char.IsDigit(keyInfo.KeyChar))
            ViewModel.AppendText(keyInfo.KeyChar.ToString());
    }

    private void HandlePaste(PasteEvent paste)
    {
        _pasteBuffer.Text = string.Empty;
        _pasteBuffer.HandlePaste(paste);
        ViewModel.AppendText(_pasteBuffer.Text);
    }

    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.Gray);

    private static Color ToColor(ConfigStatusTone tone)
        => tone switch
        {
            ConfigStatusTone.Success => Color.Green,
            ConfigStatusTone.Warning => Color.Yellow,
            ConfigStatusTone.Error => Color.Red,
            _ => Color.Gray
        };
}
