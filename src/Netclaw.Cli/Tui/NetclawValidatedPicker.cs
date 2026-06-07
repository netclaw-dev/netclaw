// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedPicker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Input;
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

internal sealed record NetclawPickerOption<TValue>(TValue Value, string Label);

internal sealed class NetclawValidatedPicker<TValue> : INetclawUiComponent
{
    private readonly NetclawUiCommit<TValue> _commit;
    private readonly NetclawUiCommitPipeline _pipeline;
    private readonly IReadOnlyList<NetclawPickerOption<TValue>> _options;
    private int _selectedIndex;

    public NetclawValidatedPicker(
        NetclawUiCommit<TValue> commit,
        NetclawUiCommitPipeline pipeline,
        IReadOnlyList<NetclawPickerOption<TValue>> options)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.Count == 0)
            throw new ArgumentException("Validated picker requires at least one option.", nameof(options));

        _selectedIndex = FindSelectedIndex(commit.ReadDraft());
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public ILayoutNode Build()
    {
        _selectedIndex = FindSelectedIndex(_commit.ReadDraft());
        var layout = Layouts.Vertical();
        for (var i = 0; i < _options.Count; i++)
        {
            var focused = i == _selectedIndex;
            var prefix = focused ? "> " : "  ";
            layout = layout.WithChild(new TextNode($"  {prefix}{_options[i].Label}").WithForeground(focused ? Color.Cyan : Color.White));
        }

        return layout;
    }

    public bool HandleInput(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                return true;
            case ConsoleKey.DownArrow:
                MoveSelection(1);
                return true;
            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                CommitSelected();
                return true;
            default:
                return true;
        }
    }

    public void HandlePaste(PasteEvent paste)
    {
        ArgumentNullException.ThrowIfNull(paste);
    }

    private void MoveSelection(int delta)
    {
        var next = Math.Clamp(_selectedIndex + delta, 0, _options.Count - 1);
        if (next == _selectedIndex)
            return;

        _selectedIndex = next;
        LastCommitResult = null;
        _commit.WriteDraft(CurrentValue);
    }

    private void CommitSelected()
    {
        var trigger = LastCommitResult?.CanSaveAnyway == true
            ? NetclawUiCommitTrigger.SaveAnyway
            : NetclawUiCommitTrigger.PickerSelection;
        LastCommitResult = _pipeline.CommitAsync(_commit, trigger)
            .GetAwaiter()
            .GetResult();
    }

    private TValue CurrentValue => _options[_selectedIndex].Value;

    private int FindSelectedIndex(TValue value)
    {
        for (var i = 0; i < _options.Count; i++)
        {
            if (EqualityComparer<TValue>.Default.Equals(_options[i].Value, value))
                return i;
        }

        return 0;
    }
}
