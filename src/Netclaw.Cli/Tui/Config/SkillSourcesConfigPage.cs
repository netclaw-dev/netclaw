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

        ViewModel.Screen.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Draft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Version.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame(ViewModel.CurrentTitle, BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() => ViewModel.Screen.Value switch
        {
            SkillSourcesScreen.Inventory => BuildInventory(),
            SkillSourcesScreen.SourceDetail => BuildSourceDetail(),
            SkillSourcesScreen.AddLocalPath => BuildTextDraft(
                "Add a local skill folder.",
                "Folder path",
                ViewModel.Draft.Value,
                "This must be an existing local directory."),
            SkillSourcesScreen.AddLocalSymlinks => BuildChoice(
                "Allow symlinks inside this folder?",
                "Symlinks can make a source scan files outside the folder.",
                ["No - stricter security", "Yes - this folder intentionally uses symlinks"]),
            SkillSourcesScreen.AddLocalName => BuildTextDraft(
                "Review local folder source.",
                "Source name",
                ViewModel.Draft.Value,
                "Enter adds the source and autosaves."),
            SkillSourcesScreen.AddRemoteUrl => BuildTextDraft(
                "Add a remote skill server.",
                "Server URL",
                ViewModel.Draft.Value,
                "Netclaw probes /.well-known/agent-skills/index.json before save."),
            SkillSourcesScreen.AddRemoteAuth => BuildChoice(
                "How should Netclaw authenticate to this server?",
                "Choose bearer token only when the server requires it.",
                ["No auth required", "Bearer token"]),
            SkillSourcesScreen.AddRemoteToken => BuildTextDraft(
                "Enter the bearer token for this skill server.",
                "Bearer token",
                string.IsNullOrWhiteSpace(ViewModel.Draft.Value) ? "(empty)" : "(new token entered)",
                "Blank tokens are not saved. Existing tokens are removed only through Remove token."),
            SkillSourcesScreen.AddRemoteName => BuildTextDraft(
                "Review remote skill server source.",
                "Source name",
                ViewModel.Draft.Value,
                "Enter adds the source and autosaves."),
            SkillSourcesScreen.RenameSource => BuildTextDraft(
                "Rename this skill source.",
                "Source name",
                ViewModel.Draft.Value,
                "Enter validates and autosaves the new name."),
            SkillSourcesScreen.ChangeLocation => BuildTextDraft(
                "Change this source location.",
                "Location",
                ViewModel.Draft.Value,
                "Enter validates and autosaves the new path or URL."),
            SkillSourcesScreen.RemoveConfirm => BuildChoice(
                "Remove this skill source from Netclaw config?",
                "This does not delete remote skills or local files.",
                ["Cancel", "Remove source"]),
            _ => Layouts.Empty(),
        });

        return _contentNode;
    }

    private ILayoutNode BuildInventory()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Skill Sources"))
            .WithChild(Hint("  Places Netclaw loads skills from. Skill enablement stays in Security & Access."))
            .WithChild(Layouts.Empty().Height(1));

        var sources = ViewModel.Sources;
        var hasLocal = sources.Any(static source => source.Kind == SkillSourceKind.LocalFolder);
        var hasRemote = sources.Any(static source => source.Kind == SkillSourceKind.RemoteSkillServer);

        if (sources.Count == 0)
        {
            layout = layout.WithChild(Hint("  No skill sources configured yet."));
        }
        else
        {
            if (hasLocal)
            {
                layout = layout.WithChild(Text("  Local folders", Color.White));
                foreach (var row in ViewModel.InventoryRows.Where(static row => row.SourceKind == SkillSourceKind.LocalFolder))
                    layout = layout.WithChild(InventoryRow(row));
                layout = layout.WithChild(Layouts.Empty().Height(1));
            }

            if (hasRemote)
            {
                layout = layout.WithChild(Text("  Remote skill servers", Color.White));
                foreach (var row in ViewModel.InventoryRows.Where(static row => row.SourceKind == SkillSourceKind.RemoteSkillServer))
                    layout = layout.WithChild(InventoryRow(row));
                layout = layout.WithChild(Layouts.Empty().Height(1));
            }
        }

        foreach (var row in ViewModel.InventoryRows.Where(static row => row.SourceKind is null))
            layout = layout.WithChild(InventoryRow(row));

        return layout;
    }

    private ILayoutNode BuildSourceDetail()
    {
        var source = ViewModel.SelectedSource;
        if (source is null)
            return Layouts.Vertical()
                .WithChild(Header("  Skill Source"))
                .WithChild(Hint("  Source no longer exists. Press Esc to return to Skill Sources."));

        var type = source.Kind == SkillSourceKind.LocalFolder ? "Local folder" : "Remote skill server";
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {source.Name}"))
            .WithChild(Text($"  Type:   {type}", Color.White))
            .WithChild(Text($"  Status: {source.StatusText}", ToColor(source.StatusTone)))
            .WithChild(Layouts.Empty().Height(1));

        foreach (var row in ViewModel.DetailRows)
            layout = layout.WithChild(DetailRow(row));

        return layout;
    }

    private ILayoutNode BuildTextDraft(string title, string fieldLabel, string value, string hint)
        => Layouts.Vertical()
            .WithChild(Header($"  {title}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Text($"  {fieldLabel}", Color.White))
            .WithChild(Text($"  {value}", Color.Cyan))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  {hint}"));

    private ILayoutNode BuildChoice(string title, string hint, IReadOnlyList<string> choices)
    {
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {title}"))
            .WithChild(Hint($"  {hint}"))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < choices.Count; i++)
        {
            var focused = i == ViewModel.SelectedRow.Value;
            var prefix = focused ? "> " : "  ";
            layout = layout.WithChild(Text($"  {prefix}{choices[i]}", focused ? Color.Cyan : Color.White));
        }

        return layout;
    }

    private ILayoutNode InventoryRow(SkillSourcesInventoryRow row)
    {
        var rows = ViewModel.InventoryRows;
        var index = IndexOf(rows, row);
        var focused = index == ViewModel.SelectedRow.Value;
        var prefix = focused ? "> " : "  ";
        var color = focused ? Color.Cyan : ToColor(row.Tone);
        return Text($"  {prefix}{row.Label,-68} {row.Detail}", color);
    }

    private ILayoutNode DetailRow(SkillSourceDetailRow row)
    {
        var rows = ViewModel.DetailRows;
        var index = IndexOf(rows, row);
        var focused = index == ViewModel.SelectedRow.Value;
        var prefix = focused ? "> " : "  ";
        var color = focused ? Color.Cyan : ToColor(row.Tone);
        return Text($"  {prefix}{row.Label,-44} {row.Detail}", color);
    }

    private static int IndexOf<T>(IReadOnlyList<T> rows, T row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(rows[i], row))
                return i;
        }

        return -1;
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone)))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
        => ViewModel.Screen
            .Select(screen => (ILayoutNode)NetclawTuiChrome.BuildKeyHintLine(KeyHints(screen)))
            .AsLayout()
            .Height(1);

    private static string KeyHints(SkillSourcesScreen screen)
        => screen switch
        {
            SkillSourcesScreen.Inventory => " [↑/↓] Navigate  [Enter] Open/Add  [Space] Toggle  [Delete] Remove  [Esc] Settings Areas  [Ctrl+Q] Quit",
            SkillSourcesScreen.SourceDetail => " [↑/↓] Navigate  [Enter/Space] Activate  [Delete] Remove  [Esc] Skill Sources  [Ctrl+Q] Quit",
            SkillSourcesScreen.AddLocalSymlinks or SkillSourcesScreen.AddRemoteAuth or SkillSourcesScreen.RemoveConfirm =>
                " [↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit",
            _ => " [Type/Paste] Edit  [Backspace] Delete  [Enter] Apply  [Esc] Back  [Ctrl+Q] Quit",
        };

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
                ViewModel.ActivateSelected();
                return;
            case ConsoleKey.Spacebar:
                if (ViewModel.IsTextEntryActive)
                {
                    ViewModel.AppendText(" ");
                    return;
                }

                ViewModel.ToggleSelected();
                return;
            case ConsoleKey.Delete:
                ViewModel.DeleteSelected();
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

    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.Gray);
    private static TextNode Text(string text, Color color) => new TextNode(text).WithForeground(color);

    private static Color ToColor(ConfigStatusTone tone)
        => tone switch
        {
            ConfigStatusTone.Success => Color.Green,
            ConfigStatusTone.Warning => Color.Yellow,
            ConfigStatusTone.Error => Color.Red,
            _ => Color.Gray,
        };
}
