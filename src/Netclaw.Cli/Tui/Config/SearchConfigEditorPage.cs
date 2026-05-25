// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorPage.cs" company="Petabridge, LLC">
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

internal sealed class SearchConfigEditorPage : ReactivePage<SearchConfigEditorViewModel>
{
    private SelectionListNode<string>? _fieldList;
    private SelectionListNode<string>? _actionList;
    private SelectionListNode<string>? _enumList;
    private SelectionListNode<string>? _dialogList;
    private TextInputNode? _textInput;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _contentSubscriptions = [];
    private FocusTarget _focusTarget = FocusTarget.FieldList;

    private enum FocusTarget
    {
        FieldList,
        FieldEditor,
        ActionList,
        Dialog,
    }

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Search")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode("  Configure how Netclaw performs web search and URL fetch augmentation.")
                .WithForeground(Color.BrightBlack))
            .WithChild(BuildContent())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _contentSubscriptions.Clear();
            _dialogList = null;
            _actionList = null;
            _enumList = null;
            _textInput = null;

            return ViewModel.ActiveDialog.Value == SearchConfigEditorDialog.ProbeWarning
                ? BuildProbeWarningDialog()
                : BuildEditorLayout();
        });

        ViewModel.SelectedIndex.Subscribe(_ => _contentNode.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.ValidationSummary.Subscribe(_ => _contentNode.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.ActiveDialog.Subscribe(_ => _contentNode.Invalidate()).DisposeWith(Subscriptions);

        return _contentNode;
    }

    private ILayoutNode BuildEditorLayout()
    {
        var rows = ViewModel.Fields.Select(FormatFieldRow).ToList();

        _fieldList = Layouts.SelectionList(rows)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        if (_focusTarget == FocusTarget.FieldList)
            _fieldList.OnFocused();

        _fieldList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                var index = rows.IndexOf(selected[0]);
                if (index >= 0)
                {
                    ViewModel.SelectedIndex.Value = index;
                    _focusTarget = FocusTarget.FieldEditor;
                    _contentNode?.Invalidate();
                    ViewModel.RequestRedraw();
                }
            })
            .DisposeWith(_contentSubscriptions);

        return Layouts.Horizontal()
            .WithChild(
                new PanelNode()
                    .WithTitle("Fields")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(
                        Layouts.Vertical()
                            .WithChild(new TextNode("  Select a field to edit.").WithForeground(Color.BrightBlack))
                            .WithChild(_fieldList))
                    .Width(42))
            .WithChild(
                Layouts.Vertical()
                    .WithSpacing(1)
                    .WithChild(BuildFieldCard())
                    .WithChild(BuildActionCard())
                    .Fill());
    }

    private string FormatFieldRow(ProjectedConfigField field)
    {
        var issues = ViewModel.GetIssues(field);
        var marker = issues.Count > 0 ? "!" : ViewModel.IsApplicable(field) ? ">" : "-";
        var value = ViewModel.IsApplicable(field)
            ? ViewModel.GetDisplayValue(field)
            : "Inactive for current backend";
        var clippedValue = value.Length > 24 ? value[..21] + "..." : value;
        return $"{marker} {field.Label,-22} {clippedValue}";
    }

    private ILayoutNode BuildFieldCard()
    {
        var field = ViewModel.SelectedField;
        var issues = ViewModel.GetIssues(field);

        var content = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {field.Label}").WithForeground(Color.White).Bold());

        if (!string.IsNullOrWhiteSpace(field.Description))
            content.WithChild(new TextNode($"  {field.Description}").WithForeground(Color.BrightBlack));

        if (!string.IsNullOrWhiteSpace(field.Hint))
            content.WithChild(new TextNode($"  {field.Hint}").WithForeground(Color.Cyan));

        if (!ViewModel.IsApplicable(field))
        {
            content.WithChild(new TextNode("  This field only matters for the currently selected backend.")
                .WithForeground(Color.BrightBlack));
            content.WithChild(new TextNode($"  {ViewModel.GetInactiveText(field)}").WithForeground(Color.BrightBlack));
        }
        else if (field.Widget == ConfigFieldWidget.EnumSelection)
        {
            var items = field.EnumOptions.Select(static option => option.Label).ToList();
            _enumList = Layouts.SelectionList(items)
                .WithMode(SelectionMode.Single)
                .WithHighlightColors(Color.Black, Color.Cyan);

            if (_focusTarget == FocusTarget.FieldEditor)
                _enumList.OnFocused();

            _enumList.SelectionConfirmed
                .Subscribe(selected =>
                {
                    if (selected.Count == 0)
                        return;

                    var option = field.EnumOptions.FirstOrDefault(o => o.Label == selected[0]);
                    if (option is not null)
                        ViewModel.SetFieldValue(field.Path, option.Value);
                })
                .DisposeWith(_contentSubscriptions);

            content.WithChild(_enumList);
        }
        else
        {
            _textInput = new TextInputNode();
            if (field.Widget == ConfigFieldWidget.PasswordInput)
                _textInput.AsPassword();
            if (!string.IsNullOrWhiteSpace(field.Placeholder))
                _textInput.WithPlaceholder(field.Placeholder);

            _textInput.Text = ViewModel.GetEditorSeed(field);
            if (_focusTarget == FocusTarget.FieldEditor)
                _textInput.OnFocused();

            _textInput.Submitted
                .Subscribe(text => ViewModel.SetFieldValue(field.Path, text))
                .DisposeWith(_contentSubscriptions);

            content.WithChild(Netclaw.Cli.Tui.Wizard.Steps.WizardStepHelpers.BuildTextInputPanel(_textInput, field.Label));
        }

        foreach (var issue in issues)
            content.WithChild(new TextNode($"  ! {issue.Message}").WithForeground(Color.Red));

        return new PanelNode()
            .WithTitle("Selected Field")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Cyan)
            .WithContent(content);
    }

    private ILayoutNode BuildActionCard()
    {
        var actions = new List<string>
        {
            "Test search backend",
            "Save settings",
            "Reset unsaved changes",
            "Back to dashboard",
        };

        _actionList = Layouts.SelectionList(actions)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Green);

        if (_focusTarget == FocusTarget.ActionList)
            _actionList.OnFocused();

        _actionList.SelectionConfirmed
            .Subscribe(async selected =>
            {
                if (selected.Count == 0)
                    return;

                switch (selected[0])
                {
                    case "Test search backend":
                        await ViewModel.TestCurrentConfigurationAsync();
                        break;
                    case "Save settings":
                        await ViewModel.SaveAsync();
                        break;
                    case "Reset unsaved changes":
                        ViewModel.ResetDraft();
                        break;
                    case "Back to dashboard":
                        ViewModel.NavigateBack();
                        break;
                }
            })
            .DisposeWith(_contentSubscriptions);

        var backendField = ViewModel.Fields.First(static f => f.Path == "Search.Backend");
        var errorCount = ViewModel.ValidationSummary.Value.Issues.Count(static i => i.Severity == ConfigValidationSeverity.Error);
        var dirtyText = ViewModel.IsDirty ? "Unsaved changes" : "No unsaved changes";
        var validationText = errorCount == 0 ? "Ready to test or save" : $"{errorCount} validation error(s)";

        return new PanelNode()
            .WithTitle("Actions")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Green)
            .WithContent(
                Layouts.Vertical()
                    .WithSpacing(1)
                    .WithChild(new TextNode($"  Backend: {ViewModel.GetDisplayValue(backendField)}").WithForeground(Color.White))
                    .WithChild(new TextNode($"  {dirtyText}").WithForeground(Color.BrightBlack))
                    .WithChild(new TextNode($"  {validationText}").WithForeground(errorCount == 0 ? Color.BrightBlack : Color.Yellow))
                    .WithChild(_actionList));
    }

    private ILayoutNode BuildProbeWarningDialog()
    {
        var options = new List<string>
        {
            "Save anyway",
            "Test again",
            "Keep editing",
        };

        _dialogList = Layouts.SelectionList(options)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);
        _dialogList.OnFocused();
        _focusTarget = FocusTarget.Dialog;

        _dialogList.SelectionConfirmed
            .Subscribe(async selected =>
            {
                if (selected.Count == 0)
                    return;

                switch (selected[0])
                {
                    case "Save anyway":
                        ViewModel.SaveWithoutProbeOverride();
                        break;
                    case "Test again":
                        ViewModel.DismissDialog();
                        _focusTarget = FocusTarget.ActionList;
                        await ViewModel.TestCurrentConfigurationAsync();
                        break;
                    default:
                        ViewModel.DismissDialog();
                        _focusTarget = FocusTarget.FieldList;
                        break;
                }
            })
            .DisposeWith(_contentSubscriptions);

        var message = ViewModel.LastProbeResult?.Message ?? "Search backend test failed.";
        return new PanelNode()
            .WithTitle("Probe Warning")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Yellow)
            .WithContent(
                Layouts.Vertical()
                    .WithSpacing(1)
                    .WithChild(new TextNode($"  {message}").WithForeground(Color.Yellow))
                    .WithChild(new TextNode("  Save anyway stores the config despite the failed runtime probe.")
                        .WithForeground(Color.BrightBlack))
                    .WithChild(_dialogList));
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.Status
            .Select(status => (ILayoutNode)(string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : new TextNode($"  {status.Text}").WithForeground(ToColor(status.Tone))))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return new TextNode(" [↑/↓] Navigate  [Enter] Confirm  [Tab] Cycle focus  [T] Test  [S] Save  [R] Reset  [Esc] Back  [Ctrl+Q] Quit")
            .WithForeground(Color.BrightBlack)
            .Height(1);
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (keyInfo.Key == ConsoleKey.T)
        {
            _focusTarget = FocusTarget.ActionList;
            _ = ViewModel.TestCurrentConfigurationAsync();
            return;
        }

        if (keyInfo.Key == ConsoleKey.S)
        {
            _focusTarget = FocusTarget.ActionList;
            _ = ViewModel.SaveAsync();
            return;
        }

        if (keyInfo.Key == ConsoleKey.R)
        {
            ViewModel.ResetDraft();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.ActiveDialog.Value != SearchConfigEditorDialog.None)
            {
                ViewModel.DismissDialog();
                _focusTarget = FocusTarget.FieldList;
                return;
            }

            ViewModel.NavigateBack();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Tab && ViewModel.ActiveDialog.Value == SearchConfigEditorDialog.None)
        {
            CycleFocus();
            return;
        }

        switch (_focusTarget)
        {
            case FocusTarget.Dialog:
                _dialogList?.HandleInput(keyInfo);
                break;
            case FocusTarget.ActionList:
                _actionList?.HandleInput(keyInfo);
                break;
            case FocusTarget.FieldEditor when _enumList is not null:
                _enumList.HandleInput(keyInfo);
                break;
            case FocusTarget.FieldEditor when _textInput is not null:
                _textInput.HandleInput(keyInfo);
                break;
            default:
                _fieldList?.HandleInput(keyInfo);
                break;
        }

        ViewModel.RequestRedraw();
    }

    private void CycleFocus()
    {
        _focusTarget = _focusTarget switch
        {
            FocusTarget.FieldList => FocusTarget.FieldEditor,
            FocusTarget.FieldEditor => FocusTarget.ActionList,
            FocusTarget.ActionList => FocusTarget.FieldList,
            _ => FocusTarget.FieldList,
        };

        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private static Color ToColor(ConfigStatusTone tone) => tone switch
    {
        ConfigStatusTone.Success => Color.Green,
        ConfigStatusTone.Warning => Color.Yellow,
        ConfigStatusTone.Error => Color.Red,
        _ => Color.White,
    };
}
