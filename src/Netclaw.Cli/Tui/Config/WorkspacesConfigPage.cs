// -----------------------------------------------------------------------
// <copyright file="WorkspacesConfigPage.cs" company="Petabridge, LLC">
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

internal sealed class WorkspacesConfigPage : ReactivePage<WorkspacesConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;
    // The picker is the screen (no Tab gate). Created once and reused so it keeps its navigation
    // state across renders; rebuilding it every frame would snap it back to the start path.
    private FilePickerNode? _directoryPicker;
    private readonly CompositeDisposable _pickerSubscriptions = [];
    // Inline "new folder" naming overlay — the picker itself cannot create directories.
    private bool _namingNewFolder;
    private string _newFolderParent = string.Empty;
    private TextInputNode? _newFolderInput;

    protected override void OnBound()
    {
        base.OnBound();
        _pickerSubscriptions.DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        ViewModel.CurrentDirectory.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.IsSaved.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);

        EnsurePicker();
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Workspaces Directory", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithChild(BuildContent().Fill())
            .WithChild(BuildStatusBar());

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            if (_namingNewFolder && _newFolderInput is not null)
            {
                return Layouts.Vertical()
                    .WithChild(Header("  New folder"))
                    .WithChild(Hint($"  Created inside: {_newFolderParent}"))
                    .WithChild(Hint("  [Enter] create   [Esc] cancel   [Ctrl+Q] quit"))
                    .WithChild(Layouts.Empty().Height(1))
                    .WithChild(NetclawTuiChrome.BuildTextInputPanel(_newFolderInput, "Folder name"));
            }

            if (_directoryPicker is null)
                return Layouts.Empty();

            // Only the picker renders a key-hint footer (Termina draws it and it can't be turned
            // off); the app-specific keys live up here so there is a single strip, not two.
            return Layouts.Vertical()
                .WithChild(Header("  Choose the workspaces directory"))
                .WithChild(Hint($"  Current: {ViewModel.CurrentDirectory.Value}"))
                .WithChild(Hint("  [Ctrl+N] new folder   [Ctrl+Q] quit"))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(_directoryPicker);
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

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (_namingNewFolder)
        {
            HandleNewFolderKey(keyInfo);
            return;
        }

        if (keyInfo.Key == ConsoleKey.N && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            BeginNewFolder();
            return;
        }

        // The picker owns every other key: arrows, Enter (open folder), Space (choose),
        // Backspace (up), Esc (cancel -> GoBack). Its events drive selection + exit.
        _directoryPicker?.HandleInput(keyInfo);
        ViewModel.RequestRedraw();
    }

    private void HandleNewFolderKey(ConsoleKeyInfo keyInfo)
    {
        if (_newFolderInput is null)
            return;

        switch (keyInfo.Key)
        {
            case ConsoleKey.Escape:
                EndNewFolder();
                return;
            case ConsoleKey.Enter:
                // On success the folder is created + saved; on failure a status error shows. Either
                // way re-create the picker so a newly created folder actually shows up, then leave
                // naming so the operator sees the result against the refreshed picker.
                ViewModel.CreateAndSelectFolder(_newFolderParent, _newFolderInput.Text);
                RecreatePickerAt(_newFolderParent);
                EndNewFolder();
                return;
            default:
                _newFolderInput.HandleInput(keyInfo);
                ViewModel.RequestRedraw();
                return;
        }
    }

    private void HandlePaste(PasteEvent paste)
    {
        if (_namingNewFolder && _newFolderInput is not null)
        {
            _newFolderInput.HandlePaste(paste);
            ViewModel.RequestRedraw();
        }
    }

    private void EnsurePicker()
    {
        if (_directoryPicker is null)
            RecreatePickerAt(ViewModel.BrowseStartPath);
    }

    // (Re)creates the picker rooted at a directory. Used on first show and to refresh the listing
    // after a new folder is created (FilePickerNode has no public reload). WithFillHeight paints
    // the full content area so it does not leave stale cells from earlier frames.
    private void RecreatePickerAt(string path)
    {
        _pickerSubscriptions.Clear();
        _directoryPicker = DirectoryPickerFactory.Build(
            path,
            ViewModel.FileSystemProvider,
            _pickerSubscriptions,
            ViewModel.ApplyPickedDirectory,
            ViewModel.GoBack);
    }

    private void BeginNewFolder()
    {
        _newFolderParent = _directoryPicker?.CurrentPath ?? ViewModel.BrowseStartPath;
        _newFolderInput = new TextInputNode().WithPlaceholder("my-workspace");
        _newFolderInput.OnFocused();
        _namingNewFolder = true;
        InvalidateAll();
    }

    private void EndNewFolder()
    {
        _namingNewFolder = false;
        _newFolderInput = null;
        InvalidateAll();
    }

    private void InvalidateAll()
    {
        _contentNode?.Invalidate();
    }

    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.Gray);

    private static Color ToColor(ConfigStatusTone tone)
        => tone switch
        {
            ConfigStatusTone.Success => Color.Green,
            ConfigStatusTone.Warning => Color.Yellow,
            ConfigStatusTone.Error => Color.Red,
            _ => Color.Gray
        };
}
