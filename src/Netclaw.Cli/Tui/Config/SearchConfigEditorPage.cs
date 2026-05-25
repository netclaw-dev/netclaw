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
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _contentSubscriptions.Clear();
            _dialogList = null;
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
        var rows = ViewModel.Fields
            .Select(field =>
            {
                var issues = ViewModel.GetIssues(field);
                var marker = issues.Count > 0 ? "!" : ViewModel.IsApplicable(field) ? " " : "-";
                var value = ViewModel.IsApplicable(field) ? ViewModel.GetDisplayValue(field) : ViewModel.GetInactiveText(field);
                return $"{marker} {field.Label,-20} {value}";
            })
            .ToList();

        _fieldList = Layouts.SelectionList(rows)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _fieldList.OnFocused();
        _focusTarget = FocusTarget.FieldList;

        _fieldList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                var index = rows.IndexOf(selected[0]);
                if (index >= 0)
                {
                    ViewModel.SelectedIndex.Value = index;
                    FocusEditor();
                }
            })
            .DisposeWith(_contentSubscriptions);

        return Layouts.Horizontal()
            .WithChild(Layouts.Vertical()
                .WithChild(new TextNode("  Search fields").WithForeground(Color.White).Bold())
                .WithChild(_fieldList)
                .Width(44))
            .WithChild(Layouts.Vertical().WithChild(BuildEditorPanel()).Fill());
    }

    private ILayoutNode BuildEditorPanel()
    {
        var field = ViewModel.SelectedField;
        var issues = ViewModel.GetIssues(field);

        var layout = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {field.Label}").WithForeground(Color.White).Bold());

        if (!string.IsNullOrWhiteSpace(field.Description))
            layout.WithChild(new TextNode($"  {field.Description}").WithForeground(Color.BrightBlack));

        if (!string.IsNullOrWhiteSpace(field.Hint))
            layout.WithChild(new TextNode($"  {field.Hint}").WithForeground(Color.Cyan));

        if (!ViewModel.IsApplicable(field))
        {
            layout.WithChild(new TextNode($"  {ViewModel.GetInactiveText(field)}").WithForeground(Color.BrightBlack));
            return layout;
        }

        if (field.Widget == ConfigFieldWidget.EnumSelection)
        {
            var items = field.EnumOptions.Select(static option => option.Label).ToList();
            _enumList = Layouts.SelectionList(items)
                .WithMode(SelectionMode.Single)
                .WithHighlightColors(Color.Black, Color.Cyan);
            _enumList.OnFocused();
            _focusTarget = FocusTarget.FieldEditor;

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

            layout.WithChild(_enumList);
        }
        else
        {
            _textInput = new TextInputNode();
            if (field.Widget == ConfigFieldWidget.PasswordInput)
                _textInput.AsPassword();
            if (!string.IsNullOrWhiteSpace(field.Placeholder))
                _textInput.WithPlaceholder(field.Placeholder);

            _textInput.Text = ViewModel.GetEditorSeed(field);
            _textInput.OnFocused();
            _focusTarget = FocusTarget.FieldEditor;

            _textInput.Submitted
                .Subscribe(text => ViewModel.SetFieldValue(field.Path, text))
                .DisposeWith(_contentSubscriptions);

            layout.WithChild(Netclaw.Cli.Tui.Wizard.Steps.WizardStepHelpers.BuildTextInputPanel(_textInput, field.Label));
        }

        foreach (var issue in issues)
            layout.WithChild(new TextNode($"  ! {issue.Message}").WithForeground(Color.Red));

        return layout;
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
                        await ViewModel.TestCurrentConfigurationAsync();
                        break;
                    default:
                        ViewModel.DismissDialog();
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
        return new TextNode(" [↑/↓] Navigate  [Enter] Edit/Confirm  [T] Test  [S] Save  [R] Reset  [Esc] Back  [Ctrl+Q] Quit")
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
            _ = ViewModel.TestCurrentConfigurationAsync();
            return;
        }

        if (keyInfo.Key == ConsoleKey.S)
        {
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
                return;
            }

            ViewModel.NavigateBack();
            return;
        }

        switch (_focusTarget)
        {
            case FocusTarget.Dialog:
                _dialogList?.HandleInput(keyInfo);
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

    private void FocusEditor()
    {
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
