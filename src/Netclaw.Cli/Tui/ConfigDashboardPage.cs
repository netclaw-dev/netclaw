// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardPage.cs" company="Petabridge, LLC">
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

namespace Netclaw.Cli.Tui;

public sealed class ConfigDashboardPage : ReactivePage<ConfigDashboardViewModel>
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
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Netclaw Config")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildList())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private ILayoutNode BuildList()
    {
        var rows = ViewModel.Items
            .Select(item => $"{item.Label,-22} {item.Description}")
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
            .WithChild(new TextNode("  Settings Areas").WithForeground(Color.White).Bold())
            .WithChild(_entryList);
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => (ILayoutNode)(string.IsNullOrWhiteSpace(msg)
                ? Layouts.Empty()
                : new TextNode($"  {msg}").WithForeground(Color.Yellow)))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return new TextNode(" [↑/↓] Navigate  [Enter] Select  [Esc] Quit  [Ctrl+Q] Quit")
            .WithForeground(Color.BrightBlack)
            .Height(1);
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
            ViewModel.RequestQuit();
            return;
        }

        _entryList?.HandleInput(keyInfo);
        ViewModel.RequestRedraw();
    }
}
