// -----------------------------------------------------------------------
// <copyright file="SecurityAccessPage.cs" company="Petabridge, LLC">
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

public sealed class SecurityAccessPage : ReactivePage<SecurityAccessViewModel>
{
    private SelectionListNode<string>? _entryList;

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Security & Access", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildList())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private ILayoutNode BuildList()
    {
        var rows = ViewModel.Items
            .Select(static item => $"{item.Label,-20} {item.Summary,-20} {item.Description}")
            .ToList();

        _entryList = Layouts.SelectionList(rows)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _entryList.OnFocused();
        _entryList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                var index = rows.IndexOf(selected[0]);
                if (index >= 0)
                {
                    ViewModel.SelectedIndex.Value = index;
                    ViewModel.ActivateSelected();
                }
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Security & Access").WithForeground(Color.White).Bold())
            .WithChild(_entryList);
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Yellow))
            .AsLayout()
            .Height(1);

    private static LayoutNode BuildKeyBindings()
        => NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Enter] Open  [Esc] Back  [Ctrl+Q] Quit");

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
            ViewModel.BackToConfig();
            return;
        }

        _entryList?.HandleInput(keyInfo);
        ViewModel.RequestRedraw();
    }
}
