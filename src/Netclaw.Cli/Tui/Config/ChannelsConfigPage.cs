// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigPage.cs" company="Petabridge, LLC">
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

public sealed class ChannelsConfigPage : ReactivePage<ChannelsConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;
    private DynamicLayoutNode? _keyBindingsNode;

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.Mode.Subscribe(_ => InvalidateAll()).DisposeWith(Subscriptions);
        ViewModel.SelectedIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Channels", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private ILayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() => ViewModel.Mode.Value switch
        {
            ChannelsConfigMode.Details => BuildProviderDetails(),
            _ => BuildProviderList()
        });

        return _contentNode;
    }

    private ILayoutNode BuildProviderList()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Chat Channels"))
            .WithChild(Hint("  Configure transport-specific chat adapters."))
            .WithChild(Layouts.Empty().Height(1));

        var items = ViewModel.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var focused = i == ViewModel.SelectedIndex.Value;
            layout = layout.WithChild(Row(
                $"{FocusPrefix(focused)}{item.Label,-14} {item.Summary,-24} {item.Description}",
                focused));
        }

        return layout;
    }

    private ILayoutNode BuildProviderDetails()
    {
        var item = ViewModel.SelectedItem;
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {item.Label} Channels"))
            .WithChild(Hint("  This view reflects current config and stored secrets."))
            .WithChild(Layouts.Empty().Height(1));

        foreach (var detail in ViewModel.SelectedDetails)
        {
            layout = layout.WithChild(new TextNode($"   {detail.Label,-18} {detail.Value}")
                .WithForeground(Color.White));
        }

        layout = layout
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint("  Editing transport fields will be added as leaf editors; this page preserves current values."));

        return layout;
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Yellow))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
    {
        _keyBindingsNode = new DynamicLayoutNode(() => NetclawTuiChrome.BuildKeyHintLine(ViewModel.Mode.Value switch
        {
            ChannelsConfigMode.Details => " [Esc] Channels  [Ctrl+Q] Quit",
            _ => " [↑/↓] Navigate  [Enter] Open  [Esc] Back  [Ctrl+Q] Quit"
        }));

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

        if (ViewModel.Mode.Value == ChannelsConfigMode.Providers)
            HandleProviderListKey(keyInfo);

        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void HandleProviderListKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveSelection(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveSelection(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateSelected();
                break;
        }
    }

    private void InvalidateAll()
    {
        _contentNode?.Invalidate();
        _keyBindingsNode?.Invalidate();
    }

    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.BrightBlack);
    private static string FocusPrefix(bool focused) => focused ? " > " : "   ";

    private static TextNode Row(string line, bool focused)
    {
        var node = new TextNode(line);
        return focused ? node.WithForeground(Color.Cyan).Bold() : node.WithForeground(Color.White);
    }
}
