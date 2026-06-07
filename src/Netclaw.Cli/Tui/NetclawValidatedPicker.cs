// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedPicker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Input;
using Termina.Layout;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

internal sealed record NetclawPickerOption<TValue>(TValue Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed class NetclawValidatedPicker<TValue> : INetclawUiComponent
{
    private readonly NetclawUiCommit<TValue> _commit;
    private readonly NetclawUiCommitPipeline _pipeline;
    private readonly IReadOnlyList<NetclawPickerOption<TValue>> _options;
    private readonly SelectionListNode<NetclawPickerOption<TValue>> _list;

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

        _list = Layouts.SelectionList<NetclawPickerOption<TValue>>(_options, static option => option.Label)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan)
            .WithHighlightedIndex(FindSelectedIndex(commit.ReadDraft()));

        _list.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                    CommitSelected(selected[0], NetclawUiCommitTrigger.PickerSelection);
            });
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public ILayoutNode Build()
    {
        _list.OnFocused();
        return _list;
    }

    public bool HandleInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Spacebar)
        {
            if (_list.HighlightedItem is { } highlighted)
                CommitSelected(highlighted.Value, NetclawUiCommitTrigger.PickerSelection);
            return true;
        }

        var handled = _list.HandleInput(keyInfo);
        if (handled && keyInfo.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Home or ConsoleKey.End)
            LastCommitResult = null;

        return handled;
    }

    public void HandlePaste(PasteEvent paste)
    {
        ArgumentNullException.ThrowIfNull(paste);
    }

    private void CommitSelected(NetclawPickerOption<TValue> option, NetclawUiCommitTrigger trigger)
    {
        _commit.WriteDraft(option.Value);
        Commit(trigger);
    }

    public NetclawUiCommitResult Commit(NetclawUiCommitTrigger trigger)
    {
        LastCommitResult = _pipeline.CommitAsync(_commit, trigger)
            .GetAwaiter()
            .GetResult();
        return LastCommitResult;
    }

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
