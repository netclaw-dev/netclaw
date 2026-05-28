// -----------------------------------------------------------------------
// <copyright file="SearchConfigEditorPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Workflow;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Config;

internal sealed class SearchConfigEditorPage : ReactivePage<SearchConfigEditorViewModel>
{
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2838", "\u2834", "\u2826", "\u2807"];
    private SelectionListNode<string>? _dialogList;
    private TextInputNode? _textInput;
    private string? _textInputFieldPath;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _contentSubscriptions = [];
    private int _providerIndex;
    private bool _providerSelectionSynced;

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();

        ViewModel.ActiveDialog.Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.CurrentScreen.Subscribe(screen =>
            {
                if (screen == SearchConfigEditorScreen.ProviderSelection)
                    _providerSelectionSynced = false;

                if (screen != SearchConfigEditorScreen.Entry)
                    ResetEntryInput();

                _contentNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);
        ViewModel.Status.Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.ValidationSummary.Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.ValidationSpinnerTick.Subscribe(_ => _contentNode?.Invalidate())
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

            if (ViewModel.ActiveDialog.Value == SearchConfigEditorDialog.ProbeWarning)
                return BuildProbeWarningDialog();

            return ViewModel.CurrentScreen.Value switch
            {
                SearchConfigEditorScreen.ProviderSelection => BuildProviderSelectionScreen(),
                SearchConfigEditorScreen.Entry => BuildEntryScreen(),
                SearchConfigEditorScreen.Validating => BuildValidatingScreen(),
                SearchConfigEditorScreen.Saved => BuildSavedScreen(),
                _ => Layouts.Empty(),
            };
        });

        return _contentNode;
    }

    private ILayoutNode BuildProviderSelectionScreen()
    {
        if (!_providerSelectionSynced)
        {
            SyncProviderIndexToCurrentBackend();
            _providerSelectionSynced = true;
        }

        return WorkflowViewComponents.BuildSelectionScreen(
            heading: "Choose the backend Netclaw uses for web search.",
            selector: BuildProviderList(),
            legend: ViewModel.ConfiguredLegend,
            supportText: ViewModel.GetProviderDescription(ViewModel.BackendOptions[_providerIndex].Value));
    }

    private ILayoutNode BuildEntryScreen()
    {
        var field = ViewModel.CurrentProviderField;

        if (field is null)
        {
            return WorkflowViewComponents.BuildSelectionScreen(
                heading: "DuckDuckGo works without setup, but may hit bot detection.",
                selector: Layouts.Empty(),
                supportText: "Press Enter to validate and save this provider selection.");
        }

        var textInput = EnsureEditingTextInput(field);
        textInput.OnFocused();

        return WorkflowViewComponents.BuildEntryScreen(
            title: ViewModel.GetEntryTitle(field),
            fieldLabel: field.Label,
            input: textInput,
            hint: ViewModel.GetEntryHint(field));
    }

    private ILayoutNode BuildValidatingScreen()
    {
        var frame = SpinnerFrames[ViewModel.ValidationSpinnerTick.Value % SpinnerFrames.Length];
        return WorkflowViewComponents.BuildValidatingScreen(
            heading: "Validating Search configuration...",
            message: $"{frame} {ViewModel.GetValidatingMessage()}",
            supportText: "This may take a few seconds.");
    }

    private ILayoutNode BuildSavedScreen()
        => WorkflowViewComponents.BuildSavedScreen(
            successText: ViewModel.GetSavedMessage(),
            nextStepText: ViewModel.GetSavedNextStepText());

    private ILayoutNode BuildProviderList()
    {
        var content = Layouts.Vertical();
        var options = ViewModel.BackendOptions;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var isFocused = i == _providerIndex;
            var isActive = string.Equals(option.Value, ViewModel.CurrentBackendValue, StringComparison.OrdinalIgnoreCase);
            var marker = isActive ? "(*)" : "( )";
            var prefix = isFocused ? ">" : " ";
            var status = IsConfigured(option.Value) ? "\u2713" : " ";
            var line = $"  {prefix} {marker} {option.Label,-20} {status}";
            var color = isFocused ? Color.Cyan : Color.White;

            var node = new TextNode(line).WithForeground(color);
            if (isActive)
                node.Bold();

            content.WithChild(node.Height(1));
        }

        return content;
    }

    private ILayoutNode BuildProbeWarningDialog()
    {
        var options = new List<string>
        {
            "Retry validation",
            "Back to edit",
            "Save anyway",
        };

        _dialogList = Layouts.SelectionList(options)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);
        _dialogList.OnFocused();

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
                    case "Retry validation":
                        ViewModel.DismissDialog();
                        await ViewModel.SubmitCurrentConfigurationAsync();
                        break;
                    default:
                        ViewModel.DismissDialog();
                        break;
                }
            })
            .DisposeWith(_contentSubscriptions);

        var message = ViewModel.LastProbeResult?.Message ?? "Search validation failed.";
        return NetclawTuiChrome.BuildPanel(
            "Search Validation Warning",
            Layouts.Vertical()
                .WithSpacing(1)
                .WithChild(new TextNode("  Netclaw could not complete a live search using this configuration.")
                    .WithForeground(Color.White))
                .WithChild(new TextNode($"  {message}").WithForeground(Color.Yellow))
                .WithChild(_dialogList),
            Color.Yellow);
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone)))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
    {
        var text = ViewModel.ActiveDialog.Value == SearchConfigEditorDialog.ProbeWarning
            ? " [↑/↓] Navigate  [Enter] Select  [Esc] Back to edit  [Ctrl+Q] Quit"
            : ViewModel.CurrentScreen.Value switch
            {
                SearchConfigEditorScreen.ProviderSelection => " [↑/↓] Navigate  [Enter] Continue  [Esc] Back  [Ctrl+Q] Quit",
                SearchConfigEditorScreen.Entry => " [Enter] Continue  [Esc] Back  [Ctrl+Q] Quit",
                SearchConfigEditorScreen.Validating => " [Ctrl+Q] Quit",
                SearchConfigEditorScreen.Saved => " [↑/↓] Navigate  [Enter] Continue  [Esc] Back  [Ctrl+Q] Quit",
                _ => " [Ctrl+Q] Quit",
            };

        return NetclawTuiChrome.BuildKeyHintLine(text);
    }

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return true;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.ActiveDialog.Value != SearchConfigEditorDialog.None)
            {
                ViewModel.DismissDialog();
                _contentNode?.Invalidate();
                return true;
            }

            if (ViewModel.CurrentScreen.Value == SearchConfigEditorScreen.Entry)
            {
                BeginProviderSelection();
                return true;
            }

            if (ViewModel.CurrentScreen.Value == SearchConfigEditorScreen.Saved)
            {
                BeginProviderSelection();
                return true;
            }

            ViewModel.NavigateBack();
            return true;
        }

        if (base.HandlePageInput(keyInfo))
            return true;

        if (ViewModel.ActiveDialog.Value == SearchConfigEditorDialog.ProbeWarning)
        {
            _dialogList?.HandleInput(keyInfo);
            return true;
        }

        if (ViewModel.CurrentScreen.Value == SearchConfigEditorScreen.ProviderSelection)
        {
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

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                var option = ViewModel.BackendOptions[_providerIndex];
                ViewModel.SelectBackendForEditing(option.Value);
                ResetEntryInput();
                _contentNode?.Invalidate();
                ViewModel.RequestRedraw();
                return true;
            }

            return true;
        }

        if (ViewModel.CurrentScreen.Value == SearchConfigEditorScreen.Saved)
        {
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

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                var option = ViewModel.BackendOptions[_providerIndex];
                ViewModel.SelectBackendForEditing(option.Value);
                ResetEntryInput();
                _contentNode?.Invalidate();
                ViewModel.RequestRedraw();
                return true;
            }

            return true;
        }

        if (ViewModel.CurrentScreen.Value == SearchConfigEditorScreen.Entry)
        {
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                StageActiveInput();
                _ = ViewModel.SubmitCurrentConfigurationAsync();
                return true;
            }

            if (_textInput is not null)
            {
                _textInput.HandleInput(keyInfo);
                ViewModel.StageFieldValue(_textInputFieldPath!, _textInput.Text);
            }

            ViewModel.RequestRedraw();
            return true;
        }

        return true;
    }

    private void BeginProviderSelection()
    {
        _providerSelectionSynced = false;
        ViewModel.BeginBackendSelection();
        ResetEntryInput();
        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void StageActiveInput()
    {
        if (_textInputFieldPath is not null && _textInput is not null)
            ViewModel.StageFieldValue(_textInputFieldPath, _textInput.Text);
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
        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void ResetEntryInput()
    {
        _textInput = null;
        _textInputFieldPath = null;
    }

    private TextInputNode EnsureEditingTextInput(ProjectedConfigField field)
    {
        if (_textInput is not null && string.Equals(_textInputFieldPath, field.Path, StringComparison.Ordinal))
            return _textInput;

        _textInput = new TextInputNode();
        _textInputFieldPath = field.Path;

        if (field.Widget == ConfigFieldWidget.PasswordInput)
            _textInput.AsPassword();
        if (!string.IsNullOrWhiteSpace(field.Placeholder))
            _textInput.WithPlaceholder(field.Placeholder);

        _textInput.Text = ViewModel.GetEditorSeed(field);
        if (!string.IsNullOrEmpty(_textInput.Text))
            _textInput.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));

        return _textInput;
    }

    private bool IsConfigured(string backend)
        => backend switch
        {
            "brave" => !string.IsNullOrWhiteSpace(ViewModel.FieldValues["Search.BraveApiKey"].Value)
                || ViewModel.HasPersistedSecret("Search.BraveApiKey"),
            "searxng" => !string.IsNullOrWhiteSpace(ViewModel.FieldValues["Search.SearXngEndpoint"].Value),
            _ => true,
        };

    private static Color ToColor(ConfigStatusTone tone) => tone switch
    {
        ConfigStatusTone.Success => Color.Green,
        ConfigStatusTone.Warning => Color.Yellow,
        ConfigStatusTone.Error => Color.Red,
        _ => Color.White,
    };
}
