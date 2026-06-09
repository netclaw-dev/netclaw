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
    private SelectionListNode<string>? _validationDialogList;
    private readonly CompositeDisposable _contentSubscriptions = [];
    private TextInputNode? _textInput;
    private SkillSourcesScreen? _textInputScreen;

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        ViewModel.Screen.Subscribe(screen =>
        {
            // Drop the active text input whenever we leave the screen that owns it so the
            // next text screen re-seeds from the view model draft.
            if (_textInputScreen is { } owner && owner != screen)
                ResetTextInput();

            _contentNode?.Invalidate();
        }).DisposeWith(Subscriptions);
        ViewModel.SelectedRow.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Draft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.Version.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.ActiveValidationDialog.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
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
        _contentNode = new DynamicLayoutNode(() =>
        {
            _contentSubscriptions.Clear();
            _validationDialogList = null;

            if (ViewModel.ActiveValidationDialog.Value is { } dialog)
                return BuildValidationDialog(dialog);

            return ViewModel.Screen.Value switch
            {
                SkillSourcesScreen.Inventory => BuildInventory(),
                SkillSourcesScreen.SourceDetail => BuildSourceDetail(),
                SkillSourcesScreen.AddLocalPath => BuildTextDraft(
                    "Add a local skill folder.",
                    "Folder path",
                    "This must be an existing local directory."),
                SkillSourcesScreen.AddLocalSymlinks => BuildChoice(
                    "Allow symlinks inside this folder?",
                    "Symlinks can make a source scan files outside the folder.",
                    ["No - stricter security", "Yes - this folder intentionally uses symlinks"]),
                SkillSourcesScreen.AddLocalName => BuildTextDraft(
                    "Review local folder source.",
                    "Source name",
                    "Enter adds the source and autosaves."),
                SkillSourcesScreen.AddRemoteUrl => BuildTextDraft(
                    "Add a remote skill server.",
                    "Server URL",
                    "Netclaw probes /.well-known/agent-skills/index.json before save.",
                    "What is a skill server?",
                    [
                        "A skill server is a Netclaw skill-server instance that publishes",
                        "agent skills over HTTP for a team or organization.",
                        "Project: https://github.com/netclaw-dev/skill-server"
                    ]),
                SkillSourcesScreen.AddRemoteAuth => BuildChoice(
                    "How should Netclaw authenticate to this server?",
                    "Choose bearer token only when the server requires it.",
                    ["No auth required", "Bearer token"]),
                SkillSourcesScreen.AddRemoteToken => BuildTextDraft(
                    "Enter the bearer token for this skill server.",
                    "Bearer token",
                    "Blank tokens are not saved. Existing tokens are removed only through Remove token.",
                    isPassword: true),
                SkillSourcesScreen.AddRemoteName => BuildTextDraft(
                    "Review remote skill server source.",
                    "Source name",
                    "Enter adds the source and autosaves."),
                SkillSourcesScreen.RenameSource => BuildTextDraft(
                    "Rename this skill source.",
                    "Source name",
                    "Enter validates and autosaves the new name."),
                SkillSourcesScreen.ChangeLocation => BuildTextDraft(
                    "Change this source location.",
                    "Location",
                    "Enter validates and autosaves the new path or URL."),
                SkillSourcesScreen.RemoveConfirm => BuildChoice(
                    "Remove this skill source from Netclaw config?",
                    "This does not delete remote skills or local files.",
                    ["Cancel", "Remove source"]),
                _ => Layouts.Empty(),
            };
        });

        return _contentNode;
    }

    private ILayoutNode BuildValidationDialog(NetclawValidationDialogModel dialog)
    {
        _validationDialogList = NetclawValidationDialogViews.BuildActionList();
        _validationDialogList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                    HandleValidationDialogAction(NetclawValidationDialogViews.ParseAction(selected[0]));
            })
            .DisposeWith(_contentSubscriptions);

        return NetclawValidationDialogViews.BuildWarningPanel(dialog, _validationDialogList);
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

    private ILayoutNode BuildTextDraft(
        string title,
        string fieldLabel,
        string hint,
        string? calloutTitle = null,
        IReadOnlyList<string>? calloutLines = null,
        bool isPassword = false)
    {
        var input = EnsureTextInput(isPassword);
        input.OnFocused();

        var layout = Layouts.Vertical()
            .WithChild(Header($"  {title}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(NetclawTuiChrome.BuildTextInputPanel(input, fieldLabel))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  {hint}"));

        if (calloutTitle is not null && calloutLines is { Count: > 0 })
            layout = layout
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(BuildCallout(calloutTitle, calloutLines));

        return layout;
    }

    private static ILayoutNode BuildCallout(string title, IReadOnlyList<string> lines)
    {
        var content = Layouts.Vertical();
        foreach (var line in lines)
            content = content.WithChild(Text($"  {line}", Color.Yellow));

        return NetclawTuiChrome.BuildPanel(title, content, Color.Yellow);
    }

    private ILayoutNode BuildChoice(string title, string hint, IReadOnlyList<string> choices)
    {
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {title}"))
            .WithChild(Hint($"  {hint}"))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < choices.Count; i++)
        {
            var focused = i == ViewModel.SelectedRow.Value;
            layout = layout.WithChild(ConfigSelectionRow.Create($"    {choices[i]}", focused));
        }

        return layout;
    }

    private ILayoutNode InventoryRow(SkillSourcesInventoryRow row)
    {
        var rows = ViewModel.InventoryRows;
        var index = IndexOf(rows, row);
        var focused = index == ViewModel.SelectedRow.Value;
        if (row.SourceKind is not null)
        {
            // Selected highlight covers the primary label line; the indented
            // detail line keeps its tone color (warning vs. neutral).
            var detailColor = row.Tone == ConfigStatusTone.Warning ? Color.Yellow : Color.Gray;
            return Layouts.Vertical()
                .WithChild(ConfigSelectionRow.Create($"    {row.Label}", focused))
                .WithChild(Text($"      {row.Detail}", detailColor));
        }

        return ConfigSelectionRow.Create($"    {row.Label,-28} {row.Detail}", focused);
    }

    private ILayoutNode DetailRow(SkillSourceDetailRow row)
    {
        var rows = ViewModel.DetailRows;
        var index = IndexOf(rows, row);
        var focused = index == ViewModel.SelectedRow.Value;
        return ConfigSelectionRow.Create($"    {row.Label,-44} {row.Detail}", focused, ToColor(row.Tone));
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
        => Observable.CombineLatest(
                ViewModel.Screen,
                ViewModel.ActiveValidationDialog,
                (screen, dialog) => (ILayoutNode)NetclawTuiChrome.BuildKeyHintLine(
                    dialog is not null
                        ? " [↑/↓] Navigate  [Enter] Select  [Esc] Back to edit  [Ctrl+Q] Quit"
                        : KeyHints(screen)))
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
            if (ViewModel.ActiveValidationDialog.Value is not null)
            {
                ViewModel.ReturnToValidationEdit();
                return;
            }

            ViewModel.GoBack();
            return;
        }

        if (ViewModel.ActiveValidationDialog.Value is not null)
        {
            _validationDialogList?.HandleInput(keyInfo);
            return;
        }

        if (TryHandleTextInput(keyInfo))
            return;

        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveSelection(-1);
                return;
            case ConsoleKey.DownArrow:
                ViewModel.MoveSelection(1);
                return;
            case ConsoleKey.Enter:
                if (TryCommitCurrentAction(ConsoleKey.Enter))
                    return;

                ViewModel.ActivateSelected();
                return;
            case ConsoleKey.Spacebar:
                if (TryCommitCurrentAction(ConsoleKey.Spacebar))
                    return;

                ViewModel.ToggleSelected();
                return;
            case ConsoleKey.Delete:
                ViewModel.DeleteSelected();
                return;
            case ConsoleKey.Backspace:
                return;
        }
    }

    private void HandlePaste(PasteEvent paste)
    {
        if (!ViewModel.IsTextEntryActive || _textInput is null)
            return;

        _textInput.HandlePaste(paste);
        ViewModel.ReplaceDraft(_textInput.Text);
        ViewModel.RequestRedraw();
    }

    private bool TryHandleTextInput(ConsoleKeyInfo keyInfo)
    {
        if (!ViewModel.IsTextEntryActive)
            return false;

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            CommitCurrentTextScreen();
            return true;
        }

        var input = EnsureTextInputForCurrentScreen();
        input.HandleInput(keyInfo);
        ViewModel.ReplaceDraft(input.Text);
        ViewModel.RequestRedraw();
        return true;
    }

    private void CommitCurrentTextScreen()
    {
        // Bracketed paste is auto-routed to the focused input by Termina, which bypasses
        // the per-keystroke draft sync. Stage the live input text before committing so a
        // paste immediately followed by Enter commits the full value, not a stale draft.
        if (_textInput is not null && _textInputScreen == ViewModel.Screen.Value)
            ViewModel.ReplaceDraft(_textInput.Text);

        var draft = ViewModel.Draft.Value;
        switch (ViewModel.Screen.Value)
        {
            case SkillSourcesScreen.AddLocalPath:
                ViewModel.CommitAddLocalPath(draft);
                break;
            case SkillSourcesScreen.AddLocalName:
                ViewModel.CommitAddLocalName(draft);
                break;
            case SkillSourcesScreen.AddRemoteUrl:
                ViewModel.CommitAddRemoteUrl(draft);
                break;
            case SkillSourcesScreen.AddRemoteToken:
                ViewModel.CommitAddRemoteToken(draft);
                break;
            case SkillSourcesScreen.AddRemoteName:
                ViewModel.CommitAddRemoteName(draft);
                break;
            case SkillSourcesScreen.RenameSource:
                ViewModel.CommitRenameSource(draft);
                break;
            case SkillSourcesScreen.ChangeLocation:
                ViewModel.CommitChangeLocation(draft);
                break;
        }
    }

    private bool TryCommitCurrentAction(ConsoleKey key)
    {
        // Choice/picker screens commit on Enter through the view model's structural-then-probe
        // commit methods so a failing probe raises the override dialog (the former picker path).
        if (key == ConsoleKey.Enter)
        {
            switch (ViewModel.Screen.Value)
            {
                case SkillSourcesScreen.AddLocalSymlinks:
                    ViewModel.CommitAddLocalSymlinks(ViewModel.SelectedRow.Value == 1);
                    return true;
                case SkillSourcesScreen.AddRemoteAuth:
                    ViewModel.CommitAddRemoteAuth(ViewModel.SelectedRow.Value == 1
                        ? SkillSourceAuthMode.BearerToken
                        : SkillSourceAuthMode.None);
                    return true;
            }
        }

        if (ViewModel.Screen.Value == SkillSourcesScreen.Inventory && key == ConsoleKey.Spacebar)
        {
            var row = ViewModel.CurrentInventoryRow;
            if (row?.Action == SkillSourcesInventoryAction.OpenSource)
            {
                ViewModel.CommitToggleEnabledAction();
                return true;
            }
        }

        if (ViewModel.Screen.Value == SkillSourcesScreen.SourceDetail)
        {
            var row = ViewModel.CurrentDetailRow;
            if (row is null)
                return false;

            switch (row.Action)
            {
                case SkillSourceDetailAction.ToggleEnabled when key is ConsoleKey.Enter or ConsoleKey.Spacebar:
                    ViewModel.CommitToggleEnabledAction();
                    return true;
                case SkillSourceDetailAction.ToggleSymlinks when key is ConsoleKey.Enter or ConsoleKey.Spacebar:
                    ViewModel.CommitToggleLocalSymlinksAction();
                    return true;
                case SkillSourceDetailAction.SyncInterval when key == ConsoleKey.Enter:
                    ViewModel.CommitCycleRemoteSyncIntervalAction();
                    return true;
                case SkillSourceDetailAction.RemoveToken when key == ConsoleKey.Enter:
                    ViewModel.CommitRemoveRemoteTokenAction();
                    return true;
                default:
                    return false;
            }
        }

        if (ViewModel.Screen.Value == SkillSourcesScreen.RemoveConfirm && key == ConsoleKey.Enter && ViewModel.SelectedRow.Value == 1)
        {
            ViewModel.CommitRemoveSourceAction();
            return true;
        }

        return false;
    }

    private void HandleValidationDialogAction(NetclawValidationDialogAction action)
    {
        switch (action)
        {
            case NetclawValidationDialogAction.RetryValidation:
                ViewModel.DismissValidationDialog();
                RetryCurrentCommit();
                break;
            case NetclawValidationDialogAction.BackToEdit:
                ViewModel.ReturnToValidationEdit();
                break;
            case NetclawValidationDialogAction.SaveAnyway:
                ViewModel.SaveCurrentDraftAnyway();
                break;
        }
    }

    private void RetryCurrentCommit()
    {
        // Re-run the same commit that raised the override dialog so the probe fires again.
        switch (ViewModel.Screen.Value)
        {
            case SkillSourcesScreen.AddRemoteAuth:
                ViewModel.CommitAddRemoteAuth(ViewModel.SelectedRow.Value == 1
                    ? SkillSourceAuthMode.BearerToken
                    : SkillSourceAuthMode.None);
                break;
            default:
                CommitCurrentTextScreen();
                break;
        }
    }

    private TextInputNode EnsureTextInputForCurrentScreen()
        => EnsureTextInput(ViewModel.Screen.Value == SkillSourcesScreen.AddRemoteToken);

    private TextInputNode EnsureTextInput(bool isPassword)
    {
        var screen = ViewModel.Screen.Value;
        if (_textInput is not null && _textInputScreen == screen)
            return _textInput;

        var input = new TextInputNode().WithPlaceholder(isPassword ? "(empty)" : "Type here...");
        if (isPassword)
            input.AsPassword();

        input.Text = ViewModel.Draft.Value;
        if (!string.IsNullOrEmpty(input.Text))
            input.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));

        _textInput = input;
        _textInputScreen = screen;
        return _textInput;
    }

    private void ResetTextInput()
    {
        _textInput = null;
        _textInputScreen = null;
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
