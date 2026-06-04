// -----------------------------------------------------------------------
// <copyright file="WorkspacesConfigPage.cs" company="Petabridge, LLC">
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

internal sealed class WorkspacesConfigPage : ReactivePage<WorkspacesConfigViewModel>
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

        ViewModel.CurrentDirectory.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.DirectoryDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.IsSaved.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Workspaces Directory", BuildInnerLayout());

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
            var draft = ViewModel.DirectoryDraft.Value;
            var candidate = string.IsNullOrWhiteSpace(draft) ? "(leave unchanged)" : draft;

            return Layouts.Vertical()
                .WithChild(Header("  Workspaces Directory"))
                .WithChild(Hint("  Sets the root Netclaw uses for project discovery and workspace-scoped prompts."))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Text($"  Current: {ViewModel.CurrentDirectory.Value}", Color.White))
                .WithChild(Text($"  New:     {candidate}", Color.Cyan))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Hint("  Type a local path. The directory is created if it does not exist."));
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
        => NetclawTuiChrome.BuildKeyHintLine(" [Type/Paste] Edit  [Backspace] Delete  [Enter] Apply  [Esc] Settings Areas  [Ctrl+Q] Quit");

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

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            ViewModel.Save();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Backspace)
        {
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
