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
    private DynamicLayoutNode? _contentNode;
    private DynamicLayoutNode? _keyBindingsNode;

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.SelectedIndex
            .Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.SelectedFeatureIndex
            .Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.EditingEnabledFeatures
            .Subscribe(_ =>
            {
                _contentNode?.Invalidate();
                _keyBindingsNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Security & Access", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private ILayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() => ViewModel.EditingEnabledFeatures.Value
            ? BuildFeatureToggles()
            : BuildSecurityMenu());

        return _contentNode;
    }

    private ILayoutNode BuildSecurityMenu()
    {
        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Security & Access").WithForeground(Color.White).Bold());

        var items = ViewModel.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var selected = i == ViewModel.SelectedIndex.Value;
            var prefix = selected ? " ▶ " : "   ";
            var line = $"{prefix}{item.Label,-20} {item.Summary,-20} {item.Description}";
            var node = new TextNode(line);
            node = selected
                ? node.WithForeground(Color.Cyan).Bold()
                : node.WithForeground(Color.White);
            layout = layout.WithChild(node);
        }

        return layout;
    }

    private ILayoutNode BuildFeatureToggles()
    {
        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Enabled Features").WithForeground(Color.White).Bold())
            .WithChild(new TextNode("  Toggle global runtime features. Audience exposure is configured separately.")
                .WithForeground(Color.BrightBlack))
            .WithChild(Layouts.Empty().Height(1));

        var names = ViewModel.FeatureNames;
        var descriptions = ViewModel.FeatureDescriptions;
        for (var i = 0; i < names.Count; i++)
        {
            var selected = i == ViewModel.SelectedFeatureIndex.Value;
            var enabled = ViewModel.IsFeatureEnabled(i);
            var prefix = selected ? " ▶ " : "   ";
            var marker = enabled ? "✓" : " ";
            var line = $"{prefix}[{marker}] {names[i],-12} {descriptions[i]}";
            var node = new TextNode(line);

            if (selected)
                node = node.WithForeground(Color.Cyan).Bold();
            else if (enabled)
                node = node.WithForeground(Color.White);
            else
                node = node.WithForeground(Color.BrightBlack);

            layout = layout.WithChild(node);
        }

        return layout;
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Yellow))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
    {
        _keyBindingsNode = new DynamicLayoutNode(() => NetclawTuiChrome.BuildKeyHintLine(
            ViewModel.EditingEnabledFeatures.Value
                ? " [↑/↓] Navigate  [Space/Enter] Toggle + Save  [Esc] Security & Access  [Ctrl+Q] Quit"
                : " [↑/↓] Navigate  [Enter] Open  [Esc] Back  [Ctrl+Q] Quit"));

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
            ViewModel.BackToConfig();
            return;
        }

        if (ViewModel.EditingEnabledFeatures.Value)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    ViewModel.MoveFeatureSelection(-1);
                    break;
                case ConsoleKey.DownArrow:
                    ViewModel.MoveFeatureSelection(1);
                    break;
                case ConsoleKey.Spacebar:
                case ConsoleKey.Enter:
                    ViewModel.ToggleSelectedFeature();
                    _contentNode?.Invalidate();
                    break;
            }

            ViewModel.RequestRedraw();
            return;
        }

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

        ViewModel.RequestRedraw();
    }
}
