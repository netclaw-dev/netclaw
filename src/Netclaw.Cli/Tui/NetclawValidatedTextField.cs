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
    ILayoutNode Build();

    bool HandleInput(ConsoleKeyInfo keyInfo);

    void HandlePaste(PasteEvent paste);
}

internal sealed class NetclawValidatedTextField : INetclawUiComponent
{
    private readonly NetclawUiCommit<string> _commit;
    private readonly NetclawUiCommitPipeline _pipeline;
    private readonly string _placeholder;
    private string _text;

    public NetclawValidatedTextField(
        NetclawUiCommit<string> commit,
        NetclawUiCommitPipeline pipeline,
        string placeholder)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        ArgumentNullException.ThrowIfNull(placeholder);

        _placeholder = placeholder;
        _text = commit.ReadDraft();
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public ILayoutNode Build()
    {
        _text = _commit.ReadDraft();
        var display = string.IsNullOrWhiteSpace(_text) ? _placeholder : _text;
        var color = string.IsNullOrWhiteSpace(_text) ? Color.BrightBlack : Color.Cyan;
        return NetclawTuiChrome.BuildPanel(_commit.Label, new TextNode($" {display}").WithForeground(color), Color.Gray)
            .Height(3);
    }

    public bool HandleInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            LastCommitResult = _pipeline.CommitAsync(_commit, NetclawUiCommitTrigger.Enter)
                .GetAwaiter()
                .GetResult();
            return true;
        }

        if (keyInfo.Key == ConsoleKey.Backspace)
        {
            if (_text.Length > 0)
                _text = _text[..^1];
            _commit.WriteDraft(_text);
            return true;
        }

        if (!char.IsControl(keyInfo.KeyChar))
        {
            _text += keyInfo.KeyChar;
            _commit.WriteDraft(_text);
        }

        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _text += paste.Content;
        _commit.WriteDraft(_text);
    }
}
