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
        return NetclawTuiChrome.BuildPageFrame("Netclaw Config", BuildInnerLayout());
    }

    private DynamicLayoutNode? _helpLineNode;

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildList())
            .WithChild(BuildHelpLine())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private ILayoutNode BuildList()
    {
        // Status-summary column: "Label   <live status>". Terminal rows (Doctor /
        // Quit) carry no status and render as the bare label.
        var rows = ViewModel.Items
            .Select(item =>
            {
                var status = ViewModel.StatusFor(item);
                return string.IsNullOrEmpty(status)
                    ? item.Label
                    : $"{item.Label,-22}  {status}";
            })
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

        _entryList.Invalidated
            .Subscribe(_ =>
            {
                var highlighted = _entryList.HighlightedItem;
                if (highlighted is not null)
                {
                    var index = rows.IndexOf(highlighted.Value);
                    if (index >= 0)
                        ViewModel.SelectedIndex.Value = index;
                }

                _helpLineNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Settings Areas").WithForeground(Color.White).Bold())
            .WithChild(_entryList);
    }

    // The focused item's description rendered as a dim help line below the list.
    private LayoutNode BuildHelpLine()
    {
        _helpLineNode = new DynamicLayoutNode(() =>
        {
            var index = Math.Clamp(ViewModel.SelectedIndex.Value, 0, ViewModel.Items.Count - 1);
            return (ILayoutNode)new TextNode($"  {ViewModel.Items[index].Description}").WithForeground(Color.BrightBlack);
        });

        return _helpLineNode.Height(1);
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Yellow))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Enter] Select  [Esc] Quit  [Ctrl+Q] Quit");
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
