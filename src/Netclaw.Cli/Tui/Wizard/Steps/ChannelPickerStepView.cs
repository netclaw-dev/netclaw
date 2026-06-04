// -----------------------------------------------------------------------
// <copyright file="ChannelPickerStepView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Input;
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the unified channel picker step.
/// Picker mode: checklist with ↑/↓ cursor, Space to toggle, Enter/E to configure, configurable done key to finish.
/// Sub-flow mode: delegates rendering and input to the active adapter's view.
/// </summary>
public sealed class ChannelPickerStepView : IWizardStepView
{
    private ChannelPickerStepViewModel? _vm;
    private StepViewCallbacks? _callbacks;

    public string StepId => WizardStepIds.ChannelPicker;
    public bool ManagesOwnFocusState => true;
    public bool CapturesInput => IsInPickerMode;

    internal bool IsInPickerMode => _vm?.IsInPickerMode ?? false;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        _vm = (ChannelPickerStepViewModel)stepVm;
        _callbacks = callbacks;

        // ManagesOwnFocusState = true prevents InitWizardPage from clearing
        // step-scoped subscriptions. Clear here to prevent accumulation across
        // sub-step transitions and cursor-blink-timer re-renders (#792).
        callbacks.Subscriptions.Clear();

        if (_vm.IsInSubFlow && _vm.ActiveAdapterVm is not null && _vm.ActiveAdapterView is not null)
        {
            _vm.ActiveAdapterView.ClearFocusState();
            return _vm.ActiveAdapterView.BuildContent(_vm.ActiveAdapterVm, callbacks);
        }

        return BuildPickerChecklist();
    }

    private ILayoutNode BuildPickerChecklist()
    {
        var adapters = _vm!.Adapters;
        var cursorIndex = _vm.CursorIndex;

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Which channels would you like to connect?").WithForeground(Color.White))
            .WithSpacing(1);

        for (var i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];
            var isFocused = i == cursorIndex;
            var isEnabled = _vm.IsAdapterEnabled(i);
            var summary = _vm.GetAdapterSummary(i);

            var prefix = isFocused ? " ▶ " : "   ";
            var checkbox = isEnabled ? "[✓]" : "[ ]";
            var name = adapter.DisplayName;
            var line = summary is not null
                ? $"{prefix}{checkbox} {name,-20} {summary}"
                : $"{prefix}{checkbox} {name}";

            var node = new TextNode(line);
            node = isFocused
                ? node.WithForeground(Color.Cyan).Bold()
                : node.WithForeground(Color.White);
            layout = layout.WithChild(node);
        }

        layout = layout.WithSpacing(1);

        var hasConfigured = _vm.AnyAdapterConfigured;
        var hintText = hasConfigured
            ? "  ↑/↓ to navigate, Space to toggle, Enter to open selected.\n  [e] Edit configured channel"
            : "  ↑/↓ to navigate, Space to toggle, Enter to configure selected.";
        if (_vm.ShowDoneAction)
        {
            hintText += hasConfigured
                ? $"    [{_vm.DoneKeyLabel}] {_vm.DoneKeyActionLabel} - {_vm.DoneActionText}"
                : $"\n  [{_vm.DoneKeyLabel}] {_vm.DoneKeyActionLabel} - {_vm.DoneActionText}";
        }

        layout = layout.WithChild(new TextNode(hintText).WithForeground(Color.BrightBlack));

        return layout;
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_vm is null || _callbacks is null) return false;

        // Sub-flow mode: delegate to active adapter's view
        if (_vm.IsInSubFlow && _vm.ActiveAdapterView is not null)
            return _vm.ActiveAdapterView.HandleKeyPress(key);

        // Picker mode: custom keyboard navigation
        var keyInfo = key.KeyInfo;
        var adapters = _vm.Adapters;

        if (_vm.ShowDoneAction && keyInfo.Key == _vm.DoneKey)
        {
            _callbacks.AdvanceStep();
            return true;
        }

        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                if (_vm.CursorIndex > 0)
                    _vm.CursorIndex--;
                _callbacks.InvalidateAndRedraw();
                return true;

            case ConsoleKey.DownArrow:
                if (_vm.CursorIndex < adapters.Count - 1)
                    _vm.CursorIndex++;
                _callbacks.InvalidateAndRedraw();
                return true;

            case ConsoleKey.Spacebar:
                _vm.ToggleAdapter(_vm.CursorIndex);
                _callbacks.InvalidateAndRedraw();
                return true;

            case ConsoleKey.Enter:
                if (_vm.IsAdapterEnabled(_vm.CursorIndex))
                {
                    // Re-enter sub-flow for editing
                    _vm.EditAdapter(_vm.CursorIndex);
                }
                else
                {
                    // Toggle on and enter sub-flow
                    _vm.ToggleAdapter(_vm.CursorIndex);
                }
                _callbacks.InvalidateAndRedraw();
                return true;

            case ConsoleKey.E:
                if (_vm.IsAdapterEnabled(_vm.CursorIndex))
                {
                    _vm.EditAdapter(_vm.CursorIndex);
                    _callbacks.InvalidateAndRedraw();
                }
                return true;

            default:
                return false;
        }
    }

    public void HandlePaste(PasteEvent paste)
    {
        if (_vm?.IsInSubFlow == true)
            _vm.ActiveAdapterView?.HandlePaste(paste);
    }

    public void ClearFocusState()
    {
        // Clear child views' focus state but preserve picker cursor position
        if (_vm is not null)
        {
            foreach (var adapter in _vm.Adapters)
                adapter.View.ClearFocusState();
        }
    }
}
