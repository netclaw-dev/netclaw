// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedTextField.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Input;
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

internal interface INetclawUiComponent
{
    NetclawUiCommitResult? LastCommitResult { get; }

    ILayoutNode Build();

    bool HandleInput(ConsoleKeyInfo keyInfo);

    void HandlePaste(PasteEvent paste);

    NetclawUiCommitResult Commit(NetclawUiCommitTrigger trigger);
}

internal sealed class NetclawValidatedTextField : INetclawUiComponent
{
    private readonly NetclawUiCommit<string> _commit;
    private readonly NetclawUiCommitPipeline _pipeline;
    private readonly string _placeholder;
    private readonly Func<string, string> _displayValue;
    private string _text;

    public NetclawValidatedTextField(
        NetclawUiCommit<string> commit,
        NetclawUiCommitPipeline pipeline,
        string placeholder,
        Func<string, string>? displayValue = null)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        ArgumentNullException.ThrowIfNull(placeholder);

        _placeholder = placeholder;
        _displayValue = displayValue ?? (static value => value);
        _text = commit.ReadDraft();
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public ILayoutNode Build()
    {
        var draft = _commit.ReadDraft();
        if (!StringComparer.Ordinal.Equals(_text, draft))
            LastCommitResult = null;

        _text = draft;
        var display = string.IsNullOrWhiteSpace(_text) ? _placeholder : _displayValue(_text);
        var color = string.IsNullOrWhiteSpace(_text) ? Color.BrightBlack : Color.Cyan;
        return NetclawTuiChrome.BuildPanel(_commit.Label, new TextNode($" {display}|").WithForeground(color), Color.Gray)
            .Height(3);
    }

    public bool HandleInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            Commit(NetclawUiCommitTrigger.Enter);
            return true;
        }

        if (keyInfo.Key == ConsoleKey.Backspace)
        {
            if (_text.Length > 0)
                _text = _text[..^1];
            LastCommitResult = null;
            _commit.WriteDraft(_text);
            return true;
        }

        if (!char.IsControl(keyInfo.KeyChar))
        {
            _text += keyInfo.KeyChar;
            LastCommitResult = null;
            _commit.WriteDraft(_text);
        }

        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _text += paste.Content;
        LastCommitResult = null;
        _commit.WriteDraft(_text);
    }

    public NetclawUiCommitResult Commit(NetclawUiCommitTrigger trigger)
    {
        LastCommitResult = _pipeline.CommitAsync(_commit, trigger)
            .GetAwaiter()
            .GetResult();
        return LastCommitResult;
    }
}
