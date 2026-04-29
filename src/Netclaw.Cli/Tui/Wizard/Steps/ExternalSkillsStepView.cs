// -----------------------------------------------------------------------
// <copyright file="ExternalSkillsStepView.cs" company="Petabridge, LLC">
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

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the External Skills wizard step.
/// Sub-step 0: checklist of detected well-known sources (custom keyboard nav).
/// Sub-step 1: optional custom path text input.
/// Sub-step 2: symlink toggle for custom path.
/// </summary>
public sealed class ExternalSkillsStepView : IWizardStepView
{
    private int _cursorIndex;
    private TextInputNode? _customPathInput;
    private SelectionListNode<string>? _symlinkList;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;
    private StepViewCallbacks? _callbacks;
    private ExternalSkillsStepViewModel? _vm;

    public string StepId => WizardStepIds.ExternalSkills;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        _callbacks = callbacks;
        _vm = (ExternalSkillsStepViewModel)stepVm;

        return _vm.CurrentSubStep switch
        {
            0 => BuildSourceChecklist(),
            1 => BuildCustomPathInput(callbacks),
            2 => BuildSymlinkToggle(callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildSourceChecklist()
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;

        var sources = _vm!.DetectedSources;
        if (_cursorIndex >= sources.Count) _cursorIndex = sources.Count - 1;
        if (_cursorIndex < 0) _cursorIndex = 0;

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  External skill directories detected:").WithForeground(Color.White))
            .WithSpacing(1);

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var isFocused = i == _cursorIndex;
            var isEnabled = _vm.IsSourceEnabled(i);
            var prefix = isFocused ? " \u25b6 " : "   ";
            var checkbox = isEnabled ? "[x]" : "[ ]";
            var line = $"{prefix}{checkbox} {source.DisplayName} ({source.ResolvedPath})";

            var node = new TextNode(line);
            node = isFocused
                ? node.WithForeground(Color.Cyan).Bold()
                : node.WithForeground(Color.White);
            layout = layout.WithChild(node);
        }

        layout = layout.WithSpacing(1)
            .WithChild(new TextNode("  Space to toggle, Enter to continue.")
                .WithForeground(Color.BrightBlack));

        return layout;
    }

    private ILayoutNode BuildCustomPathInput(StepViewCallbacks callbacks)
    {
        _lastFocusedList = null;

        _customPathInput = new TextInputNode()
            .WithPlaceholder("/path/to/team-skills");

        if (!string.IsNullOrWhiteSpace(_vm!.CustomPath))
            _customPathInput.Text = _vm.CustomPath;

        _customPathInput.OnFocused();
        _lastFocusedInput = _customPathInput;

        _customPathInput.Submitted
            .Subscribe(text =>
            {
                _vm.CustomPath = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Add a custom skill directory (optional, Enter to skip):")
                .WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(_customPathInput, "Path"));
    }

    private ILayoutNode BuildSymlinkToggle(StepViewCallbacks callbacks)
    {
        _lastFocusedInput = null;

        var noLabel = "No \u2014 stricter security (default)";
        var yesLabel = "Yes \u2014 needed if skill directory uses symlinks";

        _symlinkList = Layouts.SelectionList(noLabel, yesLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _symlinkList.OnFocused();
        _lastFocusedList = _symlinkList;

        _symlinkList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                _vm!.CustomPathAllowSymlinks = selected[0] == yesLabel;
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Allow symlinks in custom skill directory?")
                .WithForeground(Color.White))
            .WithChild(_symlinkList);
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_vm is null)
            return false;

        var keyInfo = key.KeyInfo;

        return _vm.CurrentSubStep switch
        {
            0 => HandleChecklistKey(keyInfo),
            1 when _lastFocusedInput is not null => HandleDelegatedInput(keyInfo),
            2 when _lastFocusedList is not null => HandleDelegatedList(keyInfo),
            _ => false
        };
    }

    private bool HandleChecklistKey(ConsoleKeyInfo keyInfo)
    {
        var sources = _vm!.DetectedSources;
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                if (_cursorIndex > 0) _cursorIndex--;
                break;

            case ConsoleKey.DownArrow:
                if (_cursorIndex < sources.Count - 1) _cursorIndex++;
                break;

            case ConsoleKey.Spacebar:
                if (sources.Count > 0)
                    _vm.ToggleSource(_cursorIndex);
                break;

            case ConsoleKey.Enter:
                _callbacks?.AdvanceStep();
                return true;

            default:
                return false;
        }

        _callbacks?.InvalidateAndRedraw();
        return true;
    }

    private bool HandleDelegatedInput(ConsoleKeyInfo keyInfo)
    {
        _lastFocusedInput!.HandleInput(keyInfo);
        return true;
    }

    private bool HandleDelegatedList(ConsoleKeyInfo keyInfo)
    {
        _lastFocusedList!.HandleInput(keyInfo);
        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _lastFocusedInput?.HandlePaste(paste);
    }

    public void ClearFocusState()
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;
        _customPathInput = null;
        _symlinkList = null;
        _cursorIndex = 0;
    }

}
