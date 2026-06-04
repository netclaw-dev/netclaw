// -----------------------------------------------------------------------
// <copyright file="BrowserAutomationConfigPage.cs" company="Petabridge, LLC">
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

internal sealed class BrowserAutomationConfigPage : ReactivePage<BrowserAutomationConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.Enabled.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedBackendIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Prerequisites.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Browser Automation", BuildInnerLayout());

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
            var prereq = ViewModel.Prerequisites.Value;
            var layout = Layouts.Vertical()
                .WithChild(Header("  Browser Automation"))
                .WithChild(Hint("  Adds or removes Netclaw's canonical browser MCP profile. Tool grants stay in MCP permissions."))
                .WithChild(Layouts.Empty().Height(1));

            layout = layout.WithChild(Row(0,
                $"Enabled                 [{Check(ViewModel.Enabled.Value)}]",
                "Create or remove the canonical browser MCP server profile."));
            layout = layout.WithChild(Row(1,
                $"Backend                 {ViewModel.SelectedBackendLabel}",
                $"Profile: {ViewModel.SelectedCanonicalServerName}"));
            layout = layout.WithChild(Row(2,
                "MCP permissions          open grant editor",
                "Grant browser_automation access per audience in `netclaw mcp permissions`."));

            layout = layout
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Text($"  Runtime check: {prereq.Summary}", prereq.CanEnable ? Color.Green : Color.Yellow))
                .WithChild(Hint("  Manual install guidance:"));

            if (prereq.ManualInstallSteps.Count == 0)
            {
                layout = layout.WithChild(Hint("  - No manual action detected for the selected backend."));
            }
            else
            {
                foreach (var step in prereq.ManualInstallSteps)
                    layout = layout.WithChild(Hint($"  - {step}"));
            }

            if (prereq.MissingPrerequisites.Count > 0)
                layout = layout.WithChild(Text($"  Missing: {string.Join(", ", prereq.MissingPrerequisites)}", Color.Yellow));

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
        => NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Space/Enter] Select/Save  [←/→] Backend/Save  [Esc] Settings Areas  [Ctrl+Q] Quit");

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
            case ConsoleKey.LeftArrow when ViewModel.SelectedRow.Value == 1:
                ViewModel.CycleBackend(-1);
                return;
            case ConsoleKey.RightArrow when ViewModel.SelectedRow.Value == 1:
                ViewModel.CycleBackend(1);
                return;
            case ConsoleKey.Spacebar:
            case ConsoleKey.Enter:
                ViewModel.ActivateSelected();
                return;
        }
    }

    private ILayoutNode Row(int index, string label, string description)
    {
        var focused = index == ViewModel.SelectedRow.Value;
        var prefix = focused ? "> " : "  ";
        var color = focused ? Color.Cyan : Color.White;
        return Text($"  {prefix}{label,-42} {description}", color);
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
