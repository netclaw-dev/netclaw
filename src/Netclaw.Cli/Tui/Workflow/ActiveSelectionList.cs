// -----------------------------------------------------------------------
// <copyright file="ActiveSelectionList.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Workflow;

internal sealed class ActiveSelectionList<T>
{
    private readonly IReadOnlyList<T> _options;
    private readonly Func<T, string> _labelSelector;
    private readonly Func<T, bool> _activeSelector;
    private readonly Func<T, string?>? _statusSelector;
    private readonly Action<T>? _confirmed;
    private readonly Action? _changed;
    private readonly int _labelPadWidth;
    private readonly DynamicLayoutNode _layout;

    public ActiveSelectionList(
        IReadOnlyList<T> options,
        Func<T, string> labelSelector,
        Func<T, bool> activeSelector,
        Func<T, string?>? statusSelector = null,
        Action<T>? confirmed = null,
        Action? changed = null,
        int focusedIndex = 0,
        int labelPadWidth = 0)
    {
        _options = options;
        _labelSelector = labelSelector;
        _activeSelector = activeSelector;
        _statusSelector = statusSelector;
        _confirmed = confirmed;
        _changed = changed;
        _labelPadWidth = labelPadWidth;
        FocusedIndex = ClampIndex(focusedIndex);
        _layout = new DynamicLayoutNode(BuildRows);
    }

    public int FocusedIndex { get; private set; }

    public T FocusedOption => _options[FocusedIndex];

    public ILayoutNode AsLayout() => _layout;

    public void FocusFirst(Func<T, bool> predicate)
    {
        var index = _options
            .Select((option, idx) => (option, idx))
            .FirstOrDefault(entry => predicate(entry.option))
            .idx;

        SetFocusedIndex(index, notify: false);
    }

    public void SetFocusedIndex(int index, bool notify = true)
    {
        var next = ClampIndex(index);
        if (next == FocusedIndex)
            return;

        FocusedIndex = next;
        if (notify)
            Invalidate();
        else
            _layout.Invalidate();
    }

    public bool HandleInput(ConsoleKeyInfo keyInfo)
    {
        if (_options.Count == 0)
            return false;

        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                Move(-1);
                return true;
            case ConsoleKey.DownArrow:
                Move(1);
                return true;
            case ConsoleKey.Enter:
                _confirmed?.Invoke(FocusedOption);
                return true;
            default:
                return false;
        }
    }

    public static string BuildLegend(string activeLabel, string? statusLabel = null)
        => statusLabel is null
            ? $"[x] {activeLabel}"
            : $"[x] {activeLabel}   ✓ {statusLabel}";

    private void Move(int delta) => SetFocusedIndex(FocusedIndex + delta);

    private int ClampIndex(int index)
        => _options.Count == 0
            ? 0
            : Math.Clamp(index, 0, _options.Count - 1);

    private void Invalidate()
    {
        _layout.Invalidate();
        _changed?.Invoke();
    }

    private ILayoutNode BuildRows()
    {
        var content = Layouts.Vertical();
        var clampedFocusedIndex = ClampIndex(FocusedIndex);
        for (var i = 0; i < _options.Count; i++)
        {
            var option = _options[i];
            var isFocused = i == clampedFocusedIndex;
            var isActive = _activeSelector(option);
            var prefix = isFocused ? ">" : " ";
            var checkbox = isActive ? "[x]" : "[ ]";
            var label = _labelSelector(option);
            if (_labelPadWidth > 0)
                label = label.PadRight(_labelPadWidth);

            var line = $"  {prefix} {checkbox} {label}";
            var status = _statusSelector?.Invoke(option);
            if (status is not null)
                line += $" {status}";

            var node = new TextNode(line).WithForeground(isFocused ? Color.Cyan : Color.White);
            if (isActive)
                node.Bold();

            content.WithChild(node.Height(1));
        }

        return content;
    }
}
