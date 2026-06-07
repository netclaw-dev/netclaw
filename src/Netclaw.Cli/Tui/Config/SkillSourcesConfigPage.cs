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
    private readonly NetclawUiCommitPipeline _commitPipeline = new();
    private NetclawValidatedTextField? _addLocalPathField;
    private NetclawValidatedPicker<bool>? _addLocalSymlinksPicker;
    private NetclawValidatedTextField? _addLocalNameField;
    private NetclawValidatedTextField? _addRemoteUrlField;
    private NetclawValidatedTextField? _addRemoteNameField;
    private NetclawValidatedTextField? _addRemoteTokenField;
    private NetclawValidatedPicker<SkillSourceAuthMode>? _addRemoteAuthPicker;
    private NetclawValidatedTextField? _renameSourceField;
    private NetclawValidatedTextField? _changeLocationField;

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
            if (screen != SkillSourcesScreen.AddLocalPath)
                _addLocalPathField = null;
            if (screen != SkillSourcesScreen.AddLocalSymlinks)
                _addLocalSymlinksPicker = null;
            if (screen != SkillSourcesScreen.AddLocalName)
                _addLocalNameField = null;
            if (screen != SkillSourcesScreen.AddRemoteUrl)
                _addRemoteUrlField = null;
            if (screen != SkillSourcesScreen.AddRemoteName)
                _addRemoteNameField = null;
            if (screen != SkillSourcesScreen.AddRemoteAuth)
                _addRemoteAuthPicker = null;
            if (screen != SkillSourcesScreen.AddRemoteToken)
                _addRemoteTokenField = null;
            if (screen != SkillSourcesScreen.RenameSource)
                _renameSourceField = null;
            if (screen != SkillSourcesScreen.ChangeLocation)
                _changeLocationField = null;

            _contentNode?.Invalidate();
        }).DisposeWith(Subscriptions);
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
            SkillSourcesScreen.AddLocalPath => BuildValidatedTextDraft(
                "Add a local skill folder.",
                EnsureAddLocalPathField(),
                "This must be an existing local directory."),
            SkillSourcesScreen.AddLocalSymlinks => BuildValidatedChoice(
                "Allow symlinks inside this folder?",
                "Symlinks can make a source scan files outside the folder.",
                EnsureAddLocalSymlinksPicker()),
            SkillSourcesScreen.AddLocalName => BuildValidatedTextDraft(
                "Review local folder source.",
                EnsureAddLocalNameField(),
                "Enter adds the source and autosaves."),
            SkillSourcesScreen.AddRemoteUrl => BuildValidatedTextDraft(
                "Add a remote skill server.",
                EnsureAddRemoteUrlField(),
                "Netclaw probes /.well-known/agent-skills/index.json before save.",
                "What is a skill server?",
                [
                    "A skill server is a Netclaw skill-server instance that publishes",
                    "agent skills over HTTP for a team or organization.",
                    "Project: https://github.com/netclaw-dev/skill-server"
                ]),
            SkillSourcesScreen.AddRemoteAuth => BuildValidatedChoice(
                "How should Netclaw authenticate to this server?",
                "Choose bearer token only when the server requires it.",
                EnsureAddRemoteAuthPicker()),
            SkillSourcesScreen.AddRemoteToken => BuildValidatedTextDraft(
                "Enter the bearer token for this skill server.",
                EnsureAddRemoteTokenField(),
                "Blank tokens are not saved. Existing tokens are removed only through Remove token."),
            SkillSourcesScreen.AddRemoteName => BuildValidatedTextDraft(
                "Review remote skill server source.",
                EnsureAddRemoteNameField(),
                "Enter adds the source and autosaves."),
            SkillSourcesScreen.RenameSource => BuildValidatedTextDraft(
                "Rename this skill source.",
                EnsureRenameSourceField(),
                "Enter validates and autosaves the new name."),
            SkillSourcesScreen.ChangeLocation => BuildValidatedTextDraft(
                "Change this source location.",
                EnsureChangeLocationField(),
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

    private ILayoutNode BuildValidatedTextDraft(
        string title,
        INetclawUiComponent field,
        string hint,
        string? calloutTitle = null,
        IReadOnlyList<string>? calloutLines = null)
    {
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {title}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(field.Build())
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  {hint}"));

        if (calloutTitle is not null && calloutLines is { Count: > 0 })
            layout = layout
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(BuildCallout(calloutTitle, calloutLines));

        return layout;
    }

    private NetclawValidatedTextField EnsureAddLocalPathField()
        => _addLocalPathField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.AddLocalPath(ViewModel),
            _commitPipeline,
            "Type here...");

    private NetclawValidatedPicker<bool> EnsureAddLocalSymlinksPicker()
        => _addLocalSymlinksPicker ??= new NetclawValidatedPicker<bool>(
            SkillSourcesCommitFactory.AddLocalSymlinks(ViewModel),
            _commitPipeline,
            [
                new NetclawPickerOption<bool>(false, "No - stricter security"),
                new NetclawPickerOption<bool>(true, "Yes - this folder intentionally uses symlinks"),
            ]);

    private NetclawValidatedTextField EnsureAddLocalNameField()
        => _addLocalNameField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.AddLocalName(ViewModel),
            _commitPipeline,
            "Type here...");

    private NetclawValidatedTextField EnsureAddRemoteUrlField()
        => _addRemoteUrlField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.AddRemoteUrl(ViewModel),
            _commitPipeline,
            "Type here...");

    private NetclawValidatedPicker<SkillSourceAuthMode> EnsureAddRemoteAuthPicker()
        => _addRemoteAuthPicker ??= new NetclawValidatedPicker<SkillSourceAuthMode>(
            SkillSourcesCommitFactory.AddRemoteAuth(ViewModel),
            _commitPipeline,
            [
                new NetclawPickerOption<SkillSourceAuthMode>(SkillSourceAuthMode.None, "No auth required"),
                new NetclawPickerOption<SkillSourceAuthMode>(SkillSourceAuthMode.BearerToken, "Bearer token"),
            ]);

    private NetclawValidatedTextField EnsureAddRemoteTokenField()
        => _addRemoteTokenField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.AddRemoteToken(ViewModel),
            _commitPipeline,
            "(empty)",
            static _ => "(new token entered)");

    private NetclawValidatedTextField EnsureAddRemoteNameField()
        => _addRemoteNameField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.AddRemoteName(ViewModel),
            _commitPipeline,
            "Type here...");

    private NetclawValidatedTextField EnsureRenameSourceField()
        => _renameSourceField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.RenameSource(ViewModel),
            _commitPipeline,
            "Type here...");

    private NetclawValidatedTextField EnsureChangeLocationField()
        => _changeLocationField ??= new NetclawValidatedTextField(
            SkillSourcesCommitFactory.ChangeLocation(ViewModel),
            _commitPipeline,
            "Type here...");

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
            var prefix = focused ? "> " : "  ";
            layout = layout.WithChild(Text($"  {prefix}{choices[i]}", focused ? Color.Cyan : Color.White));
        }

        return layout;
    }

    private static ILayoutNode BuildValidatedChoice(string title, string hint, INetclawUiComponent picker)
        => Layouts.Vertical()
            .WithChild(Header($"  {title}"))
            .WithChild(Hint($"  {hint}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(picker.Build());

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

        if (CurrentValidatedComponent()?.HandleInput(keyInfo) == true)
        {
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
        if (CurrentValidatedComponent() is { } field)
        {
            field.HandlePaste(paste);
            return;
        }
    }

    private INetclawUiComponent? CurrentValidatedComponent()
        => ViewModel.Screen.Value switch
        {
            SkillSourcesScreen.AddLocalPath => EnsureAddLocalPathField(),
            SkillSourcesScreen.AddLocalSymlinks => EnsureAddLocalSymlinksPicker(),
            SkillSourcesScreen.AddLocalName => EnsureAddLocalNameField(),
            SkillSourcesScreen.AddRemoteUrl => EnsureAddRemoteUrlField(),
            SkillSourcesScreen.AddRemoteAuth => EnsureAddRemoteAuthPicker(),
            SkillSourcesScreen.AddRemoteToken => EnsureAddRemoteTokenField(),
            SkillSourcesScreen.AddRemoteName => EnsureAddRemoteNameField(),
            SkillSourcesScreen.RenameSource => EnsureRenameSourceField(),
            SkillSourcesScreen.ChangeLocation => EnsureChangeLocationField(),
            _ => null,
        };

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
