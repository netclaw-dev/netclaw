// -----------------------------------------------------------------------
// <copyright file="SkillSourcesConfigPage.cs" company="Petabridge, LLC">
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

internal sealed class SkillSourcesConfigPage : ReactivePage<SkillSourcesConfigViewModel>
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

        ViewModel.ExternalSourceCount.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SkillFeedCount.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.HasPersistedFeedApiKey.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.ExternalDirectoryDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SkillFeedUrlDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SkillFeedApiKeyDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Skill Sources", BuildInnerLayout());

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
            var apiKeyState = ViewModel.HasPersistedFeedApiKey.Value && string.IsNullOrWhiteSpace(ViewModel.SkillFeedApiKeyDraft.Value)
                ? "(stored token preserved)"
                : string.IsNullOrWhiteSpace(ViewModel.SkillFeedApiKeyDraft.Value) ? "(optional)" : "(new token entered)";

            return Layouts.Vertical()
                .WithChild(Header("  Skill Sources"))
                .WithChild(Hint("  Configure external skill directories and private skill feeds. Skill feature enablement stays in Security & Access."))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Hint($"  Current: external directories={ViewModel.ExternalSourceCount.Value}, skill feeds={ViewModel.SkillFeedCount.Value}"))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(Row(0,
                    $"External skill directory  {DisplayDraft(ViewModel.ExternalDirectoryDraft.Value)}",
                    "Existing local directory; saved as ExternalSkills.Sources."))
                .WithChild(Row(1,
                    $"Skill feed URL            {DisplayDraft(ViewModel.SkillFeedUrlDraft.Value)}",
                    "HTTP(S) skill-server base URL; discovery is probed before save."))
                .WithChild(Row(2,
                    $"Skill feed API key        {apiKeyState}",
                    "Optional bearer token; leave blank to preserve the stored token."));
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
        => NetclawTuiChrome.BuildKeyHintLine(" [Up/Down] Navigate  [Type/Paste] Edit  [Backspace] Delete  [Enter] Apply  [Esc] Settings Areas  [Ctrl+Q] Quit");

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
            case ConsoleKey.Enter:
                ViewModel.Save();
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

    private ILayoutNode Row(int index, string label, string description)
    {
        var focused = index == ViewModel.SelectedRow.Value;
        var prefix = focused ? "> " : "  ";
        var color = focused ? Color.Cyan : Color.White;
        return Text($"  {prefix}{label,-58} {description}", color);
    }

    private static string DisplayDraft(string value) => string.IsNullOrWhiteSpace(value) ? "(leave unchanged)" : value;
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
