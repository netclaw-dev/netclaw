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

        ViewModel.Mode.Subscribe(_ => InvalidateAll()).DisposeWith(Subscriptions);
        ViewModel.SelectedIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedPostureIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedCascadeIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedFeatureIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedAudienceIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedAudienceRowIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
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
        _contentNode = new DynamicLayoutNode(() => ViewModel.Mode.Value switch
        {
            SecurityAccessEditorMode.Posture => BuildPostureEditor(),
            SecurityAccessEditorMode.PostureCascade => BuildPostureCascade(),
            SecurityAccessEditorMode.Features => BuildFeatureToggles(),
            SecurityAccessEditorMode.AudienceList => BuildAudienceList(),
            SecurityAccessEditorMode.AudienceProfile => BuildAudienceProfile(),
            _ => BuildSecurityMenu()
        });

        return _contentNode;
    }

    private ILayoutNode BuildSecurityMenu()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Security & Access"));

        var items = ViewModel.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            layout = layout.WithChild(Row(
                $"{FocusPrefix(i == ViewModel.SelectedIndex.Value)}{item.Label,-20} {item.Summary,-20} {item.Description}",
                i == ViewModel.SelectedIndex.Value));
        }

        return layout;
    }

    private ILayoutNode BuildPostureEditor()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Security Posture"))
            .WithChild(Hint($"  Current posture: {ViewModel.CurrentPosture}"))
            .WithChild(Layouts.Empty().Height(1));

        var options = ViewModel.PostureOptions;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var focused = i == ViewModel.SelectedPostureIndex.Value;
            var active = option.Value == ViewModel.CurrentPosture;
            layout = layout.WithChild(Row(
                $"{FocusPrefix(focused)}[{Check(active)}] {option.Label,-10} {option.Description}",
                focused,
                active));
        }

        return layout;
    }

    private ILayoutNode BuildPostureCascade()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Posture change affects Audience Profiles"))
            .WithChild(Hint("  You have customized Audience Profiles. Changing posture can overwrite them."))
            .WithChild(Layouts.Empty().Height(1));

        var options = ViewModel.PostureCascadeOptions;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var focused = i == ViewModel.SelectedCascadeIndex.Value;
            layout = layout.WithChild(Row(
                $"{FocusPrefix(focused)}{option.Label,-42} {option.Description}",
                focused));
        }

        return layout;
    }

    private ILayoutNode BuildFeatureToggles()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Enabled Features"))
            .WithChild(Hint("  Toggle global runtime features. Audience exposure is configured separately."))
            .WithChild(Layouts.Empty().Height(1));

        var names = ViewModel.FeatureNames;
        var descriptions = ViewModel.FeatureDescriptions;
        for (var i = 0; i < names.Count; i++)
        {
            var focused = i == ViewModel.SelectedFeatureIndex.Value;
            var enabled = ViewModel.IsFeatureEnabled(i);
            layout = layout.WithChild(Row(
                $"{FocusPrefix(focused)}[{Check(enabled)}] {names[i],-12} {descriptions[i]}",
                focused,
                enabled));
        }

        return layout;
    }

    private ILayoutNode BuildAudienceList()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Audience Profiles"))
            .WithChild(Hint($"  System default posture: {ViewModel.CurrentPosture}"))
            .WithChild(Hint("  Customize audience/channel access when it should differ."))
            .WithChild(Legend("  * global default audience   Customized = custom overrides"))
            .WithChild(Layouts.Empty().Height(1));

        var options = ViewModel.AudienceOptions;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var focused = i == ViewModel.SelectedAudienceIndex.Value;
            var marker = ViewModel.AudienceOverrideMarker(option.Value);
            var defaultMarker = ViewModel.IsSystemDefaultAudience(option.Value) ? "*" : " ";
            layout = layout.WithChild(Row(
                $"{FocusPrefix(focused)}{defaultMarker} {option.Label,-9} {option.Description,-34} {marker}",
                focused));
        }

        return layout;
    }

    private ILayoutNode BuildAudienceProfile()
    {
        var audience = ViewModel.AudienceOptions[ViewModel.SelectedAudienceIndex.Value];
        var layout = Layouts.Vertical()
            .WithChild(Header($"  Audience Profile: {audience.Label}"))
            .WithChild(Hint($"  System default posture: {ViewModel.CurrentPosture}"))
            .WithChild(Hint($"  Profile: {ViewModel.SelectedAudienceOverrideStatus}"))
            .WithChild(Layouts.Empty().Height(1));

        var rows = ViewModel.ProfileRows;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var focused = i == ViewModel.SelectedAudienceRowIndex.Value;
            if (row.Kind == AudienceProfileRowKind.FileTools)
                layout = layout.WithChild(Section("  Tools"));
            if (row.Kind == AudienceProfileRowKind.FileAccess)
                layout = layout.WithChild(Layouts.Empty().Height(1)).WithChild(Section("  Access"));
            if (row.Kind == AudienceProfileRowKind.ResetToDefault)
                layout = layout.WithChild(Layouts.Empty().Height(1)).WithChild(Section("  Actions"));

            var line = row.Kind switch
            {
                AudienceProfileRowKind.FileAccess or AudienceProfileRowKind.IncomingAttachments =>
                    $"{FocusPrefix(focused)}{row.Label,-14} {CycleValue(ViewModel.AudienceValue(row.Kind))}",
                AudienceProfileRowKind.McpPermissions =>
                    $"{FocusPrefix(focused)}{row.Label,-14} [Open] {ViewModel.AudienceValue(row.Kind)}",
                AudienceProfileRowKind.ResetToDefault =>
                    $"{FocusPrefix(focused)}{row.Label,-14} [Reset]",
                _ =>
                    $"{FocusPrefix(focused)}[{Check(ViewModel.IsAudienceToggleEnabled(row.Kind))}] {row.Label}"
            };

            var enabled = row.Kind switch
            {
                AudienceProfileRowKind.FileAccess or AudienceProfileRowKind.IncomingAttachments or AudienceProfileRowKind.McpPermissions or AudienceProfileRowKind.ResetToDefault => true,
                _ => ViewModel.IsAudienceToggleEnabled(row.Kind)
            };
            layout = layout.WithChild(Row(line, focused, enabled));
        }

        var focusedRow = rows[ViewModel.SelectedAudienceRowIndex.Value];
        layout = layout
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  {ViewModel.AudienceRowHelp(focusedRow.Kind)}"));

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
            SecurityAccessEditorMode.Posture => " [↑/↓] Navigate  [Enter] Apply  [Esc] Security & Access  [Ctrl+Q] Quit",
            SecurityAccessEditorMode.PostureCascade => " [↑/↓] Navigate  [Enter] Apply  [Esc] Back  [Ctrl+Q] Quit",
            SecurityAccessEditorMode.Features => " [↑/↓] Navigate  [Space/Enter] Toggle/Save  [Esc] Security & Access  [Ctrl+Q] Quit",
            SecurityAccessEditorMode.AudienceList => " [↑/↓] Navigate  [Enter] Edit Audience  [Esc] Security & Access  [Ctrl+Q] Quit",
            SecurityAccessEditorMode.AudienceProfile => " [↑/↓] Navigate  [←/→] Change  [Space/Enter] Toggle/Apply  [Esc] Audiences  [Ctrl+Q] Quit",
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

        switch (ViewModel.Mode.Value)
        {
            case SecurityAccessEditorMode.Menu:
                HandleMenuKey(keyInfo);
                break;
            case SecurityAccessEditorMode.Posture:
                HandlePostureKey(keyInfo);
                break;
            case SecurityAccessEditorMode.PostureCascade:
                HandleCascadeKey(keyInfo);
                break;
            case SecurityAccessEditorMode.Features:
                HandleFeatureKey(keyInfo);
                break;
            case SecurityAccessEditorMode.AudienceList:
                HandleAudienceListKey(keyInfo);
                break;
            case SecurityAccessEditorMode.AudienceProfile:
                HandleAudienceProfileKey(keyInfo);
                break;
        }

        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void HandleMenuKey(ConsoleKeyInfo keyInfo)
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

    private void HandlePostureKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MovePostureSelection(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MovePostureSelection(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ApplySelectedPosture();
                break;
        }
    }

    private void HandleCascadeKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveCascadeSelection(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveCascadeSelection(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ApplySelectedCascadeOption();
                break;
        }
    }

    private void HandleFeatureKey(ConsoleKeyInfo keyInfo)
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
                break;
        }
    }

    private void HandleAudienceListKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveAudienceSelection(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveAudienceSelection(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.OpenSelectedAudienceProfile();
                break;
        }
    }

    private void HandleAudienceProfileKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveAudienceRow(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveAudienceRow(1);
                break;
            case ConsoleKey.LeftArrow:
                ViewModel.ChangeSelectedAudienceProfileRow(-1);
                break;
            case ConsoleKey.RightArrow:
                ViewModel.ChangeSelectedAudienceProfileRow(1);
                break;
            case ConsoleKey.Spacebar:
            case ConsoleKey.Enter:
                ViewModel.ActivateSelectedAudienceProfileRow();
                break;
        }
    }

    private void InvalidateAll()
    {
        _contentNode?.Invalidate();
        _keyBindingsNode?.Invalidate();
    }

    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Section(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Legend(string text) => new TextNode(text).WithForeground(Color.White);
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.BrightBlack);

    // Constant indent so non-selected rows keep the same content column the
    // focused full-width bar uses (the bar replaces the old ▶ marker).
    private static string FocusPrefix(bool focused) => "   ";
    private static string Check(bool enabled) => enabled ? "✓" : " ";
    private static string CycleValue(string value) => $"[◀ {value,-17} ▶]";

    private static ILayoutNode Row(string line, bool focused, bool enabled = true)
        => ConfigSelectionRow.Create(line, focused, enabled ? Color.White : Color.BrightBlack);
}
