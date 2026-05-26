// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Netclaw.Cli.Tui;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Config;

internal sealed class SearchConfigEditorPage : ReactivePage<SearchConfigEditorViewModel>
{
    private SelectionListNode<string>? _dialogList;
    private TextInputNode? _textInput;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _contentSubscriptions = [];
    private SearchFocusTarget _focusTarget = SearchFocusTarget.ProviderList;
    private int _providerIndex;
    private string? _editingFieldPath;
    private string _editSeed = string.Empty;

    private enum SearchFocusTarget
    {
        ProviderList,
        FieldInput,
        Dialog,
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();

        ViewModel.ActiveDialog.Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.ValidationSummary.Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.Status.Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Search", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _contentSubscriptions.Clear();
            _dialogList = null;
            _textInput = null;

            if (ViewModel.ActiveDialog.Value == SearchConfigEditorDialog.ProbeWarning)
                return BuildProbeWarningDialog();

            return BuildProviderMatrixScreen();
        });

        return _contentNode;
    }

    private ILayoutNode BuildProviderMatrixScreen()
    {
        SyncProviderIndexToCurrentBackend();

        var content = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode("  Choose your web search provider:").WithForeground(Color.White))
            .WithChild(BuildProviderList())
            .WithChild(BuildProviderDetails())
            .WithChild(BuildMatrixState())
            .WithChild(BuildCommandRail());

        return content;
    }

    private ILayoutNode BuildProviderList()
    {
        var content = Layouts.Vertical();
        var options = ViewModel.BackendOptions;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var isFocused = _focusTarget == SearchFocusTarget.ProviderList && i == _providerIndex;
            var isActive = string.Equals(option.Value, ViewModel.CurrentBackendValue, StringComparison.OrdinalIgnoreCase);
            var marker = isActive ? "(*)" : "( )";
            var prefix = isFocused ? ">" : " ";
            var line = $"  {prefix} {marker} {option.Label,-18} {GetProviderRequirementText(option.Value)}";
            var color = isFocused ? Color.Cyan : Color.White;

            var node = new TextNode(line).WithForeground(color);
            if (isActive)
                node.Bold();

            content.WithChild(node.Height(1));
        }

        return content;
    }

    private ILayoutNode BuildProviderDetails()
    {
        var content = Layouts.Vertical().WithSpacing(1);

        content.WithChild(new TextNode($"  {ViewModel.CurrentBackendLabel}").WithForeground(Color.White).Bold());

        var field = ViewModel.CurrentProviderField;
        if (field is null)
        {
            content.WithChild(new TextNode("  No additional setup required.").WithForeground(Color.Gray));
            return content;
        }

        content.WithChild(IsEditingField(field)
            ? BuildEditingFieldLayout(field)
            : BuildReadonlyFieldLayout(field));

        foreach (var issue in ViewModel.GetCurrentProviderIssues())
            content.WithChild(new TextNode($"  ! {issue.Message}").WithForeground(Color.Red));

        return content;
    }

    private ILayoutNode BuildReadonlyFieldLayout(ProjectedConfigField field)
    {
        var displayValue = ViewModel.GetDisplayValue(field);
        if (string.IsNullOrWhiteSpace(displayValue))
            displayValue = "(not configured)";

        var valueColor = displayValue.StartsWith("(", StringComparison.Ordinal)
            ? Color.Gray
            : Color.White;

        var content = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {field.Label}:").WithForeground(Color.White))
            .WithChild(new TextNode($"  {displayValue}").WithForeground(valueColor))
            .WithChild(new TextNode("  Press Enter to edit.").WithForeground(Color.Gray));

        var supportText = ViewModel.GetCurrentProviderSupportText();
        if (!string.IsNullOrWhiteSpace(supportText))
            content.WithChild(new TextNode($"  {supportText}").WithForeground(Color.Gray));

        return content;
    }

    private ILayoutNode BuildEditingFieldLayout(ProjectedConfigField field)
    {
        var content = Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode($"  {field.Label}:").WithForeground(Color.White));

        _textInput = new TextInputNode();
        if (field.Widget == ConfigFieldWidget.PasswordInput)
            _textInput.AsPassword();
        if (!string.IsNullOrWhiteSpace(field.Placeholder))
            _textInput.WithPlaceholder(field.Placeholder);

        _textInput.Text = ViewModel.GetEditorSeed(field);
        if (_focusTarget == SearchFocusTarget.FieldInput)
            _textInput.OnFocused();

        _textInput.Submitted
            .Subscribe(text =>
            {
                var result = ViewModel.CommitField(field.Path, text);
                if (!result.Success)
                {
                    ViewModel.RequestRedraw();
                    return;
                }

                _editingFieldPath = null;
                _editSeed = string.Empty;
                _focusTarget = SearchFocusTarget.ProviderList;
                _contentNode?.Invalidate();
                ViewModel.RequestRedraw();
            })
            .DisposeWith(_contentSubscriptions);

        content.WithChild(NetclawTuiChrome.BuildTextInputPanel(_textInput, field.Label));
        content.WithChild(new TextNode("  Press Enter to apply or Esc to cancel edit.").WithForeground(Color.Gray));

        var supportText = ViewModel.GetCurrentProviderSupportText();
        if (!string.IsNullOrWhiteSpace(supportText))
            content.WithChild(new TextNode($"  {supportText}").WithForeground(Color.Gray));

        return content;
    }

    private ILayoutNode BuildMatrixState()
    {
        var children = Layouts.Vertical().WithSpacing(1);
        var hasState = false;

        if (ViewModel.GetSummaryStateTone() == ConfigStatusTone.Warning)
        {
            children.WithChild(new TextNode($"  {ViewModel.GetSummaryStateText()}").WithForeground(Color.Yellow));
            hasState = true;
        }

        if (ViewModel.IsDirty)
        {
            children.WithChild(new TextNode("  Unsaved changes.").WithForeground(Color.Yellow));
            hasState = true;
        }

        if (ViewModel.LastProbeResult is { } lastProbe)
        {
            children.WithChild(new TextNode($"  Last test: {lastProbe.Message}").WithForeground(ToColor(lastProbe.Tone)));
            hasState = true;
        }

        return hasState ? children : Layouts.Empty();
    }

    private ILayoutNode BuildCommandRail()
    {
        var text = _focusTarget == SearchFocusTarget.FieldInput
            ? "  [Enter] Apply   [Esc] Cancel edit"
            : ViewModel.CurrentProviderField is null
                ? "  [T] Test   [S] Save   [Esc] Back"
                : "  [Enter] Edit   [T] Test   [S] Save   [Esc] Back";

        return new TextNode(text).WithForeground(Color.Gray);
    }

    private ILayoutNode BuildProbeWarningDialog()
    {
        var options = new List<string>
        {
            "Keep editing",
            "Test again",
            "Save anyway",
        };

        _dialogList = Layouts.SelectionList(options)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);
        _dialogList.OnFocused();
        _focusTarget = SearchFocusTarget.Dialog;

        _dialogList.SelectionConfirmed
            .Subscribe(async selected =>
            {
                if (selected.Count == 0)
                    return;

                switch (selected[0])
                {
                    case "Save anyway":
                        ViewModel.SaveWithoutProbeOverride();
                        _focusTarget = SearchFocusTarget.ProviderList;
                        break;
                    case "Test again":
                        ViewModel.DismissDialog();
                        _focusTarget = SearchFocusTarget.ProviderList;
                        await ViewModel.TestCurrentConfigurationAsync();
                        break;
                    default:
                        ViewModel.DismissDialog();
                        _focusTarget = SearchFocusTarget.ProviderList;
                        break;
                }
            })
            .DisposeWith(_contentSubscriptions);

        var message = ViewModel.LastProbeResult?.Message ?? "Search backend test failed.";
        return NetclawTuiChrome.BuildPanel(
            "Search Test Warning",
            Layouts.Vertical()
                .WithSpacing(1)
                .WithChild(new TextNode($"  {message}").WithForeground(Color.Yellow))
                .WithChild(new TextNode("  Netclaw could not complete a live search using this configuration.")
                    .WithForeground(Color.Gray))
                .WithChild(_dialogList),
            Color.Yellow);
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone)))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
    {
        var text = _focusTarget switch
        {
            SearchFocusTarget.Dialog => " [↑/↓] Navigate  [Enter] Confirm  [Esc] Dismiss  [Ctrl+Q] Quit",
            SearchFocusTarget.FieldInput => " [Enter] Apply  [Esc] Cancel edit  [Ctrl+Q] Quit",
            _ when ViewModel.CurrentProviderField is null => " [↑/↓] Navigate  [T] Test  [S] Save  [Esc] Back  [Ctrl+Q] Quit",
            _ => " [↑/↓] Navigate  [Enter] Edit  [T] Test  [S] Save  [Esc] Back  [Ctrl+Q] Quit",
        };

        return NetclawTuiChrome.BuildKeyHintLine(text);
    }

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (base.HandlePageInput(keyInfo))
            return true;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return true;
        }

        if (_focusTarget != SearchFocusTarget.FieldInput && keyInfo.Key == ConsoleKey.T)
        {
            _ = ViewModel.TestCurrentConfigurationAsync();
            return true;
        }

        if (_focusTarget != SearchFocusTarget.FieldInput && keyInfo.Key == ConsoleKey.S)
        {
            _ = ViewModel.SaveAsync();
            return true;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.ActiveDialog.Value != SearchConfigEditorDialog.None)
            {
                ViewModel.DismissDialog();
                _focusTarget = SearchFocusTarget.ProviderList;
                return true;
            }

            if (_focusTarget == SearchFocusTarget.FieldInput)
            {
                CancelActiveEdit();
                return true;
            }

            ViewModel.NavigateBack();
            return true;
        }

        if (_focusTarget != SearchFocusTarget.FieldInput && keyInfo.Key == ConsoleKey.Enter)
        {
            BeginInlineEdit();
            return true;
        }

        switch (_focusTarget)
        {
            case SearchFocusTarget.Dialog:
                _dialogList?.HandleInput(keyInfo);
                break;
            case SearchFocusTarget.FieldInput when _textInput is not null:
                _textInput.HandleInput(keyInfo);
                break;
            default:
                if (keyInfo.Key == ConsoleKey.UpArrow)
                {
                    MoveProviderSelection(-1);
                    return true;
                }

                if (keyInfo.Key == ConsoleKey.DownArrow)
                {
                    MoveProviderSelection(1);
                    return true;
                }

                break;
        }

        ViewModel.RequestRedraw();
        return true;
    }

    private void SyncProviderIndexToCurrentBackend()
    {
        var index = ViewModel.BackendOptions
            .Select((option, idx) => (option, idx))
            .FirstOrDefault(entry => string.Equals(entry.option.Value, ViewModel.CurrentBackendValue, StringComparison.OrdinalIgnoreCase))
            .idx;

        _providerIndex = Math.Clamp(index, 0, Math.Max(0, ViewModel.BackendOptions.Count - 1));
    }

    private void MoveProviderSelection(int delta)
    {
        if (ViewModel.BackendOptions.Count == 0)
            return;

        var next = Math.Clamp(_providerIndex + delta, 0, ViewModel.BackendOptions.Count - 1);
        if (next == _providerIndex)
            return;

        _providerIndex = next;
        _editingFieldPath = null;
        _editSeed = string.Empty;

        var option = ViewModel.BackendOptions[_providerIndex];
        ViewModel.SelectBackendForEditing(option.Value);
        _contentNode?.Invalidate();
    }

    private void BeginInlineEdit()
    {
        if (ViewModel.CurrentProviderField is not { } field)
            return;

        _editingFieldPath = field.Path;
        _editSeed = ViewModel.GetEditorSeed(field);
        _focusTarget = SearchFocusTarget.FieldInput;
        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private bool IsEditingField(ProjectedConfigField field)
        => _focusTarget == SearchFocusTarget.FieldInput
            && string.Equals(_editingFieldPath, field.Path, StringComparison.Ordinal);

    private void CancelActiveEdit()
    {
        if (_editingFieldPath is { } path)
            ViewModel.CommitField(path, _editSeed);

        _editingFieldPath = null;
        _editSeed = string.Empty;
        _focusTarget = SearchFocusTarget.ProviderList;
        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private static string GetProviderRequirementText(string backend)
        => backend switch
        {
            "brave" => "Requires API key",
            "searxng" => "Requires endpoint URL",
            _ => "No setup required",
        };

    private static Color ToColor(ConfigStatusTone tone) => tone switch
    {
        ConfigStatusTone.Success => Color.Green,
        ConfigStatusTone.Warning => Color.Yellow,
        ConfigStatusTone.Error => Color.Red,
        _ => Color.White,
    };
}
