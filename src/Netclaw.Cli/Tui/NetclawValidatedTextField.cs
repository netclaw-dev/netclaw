// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedTextField.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Input;
using Termina.Layout;
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
    private readonly TextInputNode _input;
    private string _lastObservedDraft;

    public NetclawValidatedTextField(
        NetclawUiCommit<string> commit,
        NetclawUiCommitPipeline pipeline,
        string placeholder,
        bool isPassword = false)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        ArgumentNullException.ThrowIfNull(placeholder);

        _lastObservedDraft = commit.ReadDraft();
        _input = new TextInputNode().WithPlaceholder(placeholder);
        if (isPassword)
            _input.AsPassword();

        _input.Text = _lastObservedDraft;
        if (!string.IsNullOrEmpty(_input.Text))
            MoveCursorToEnd();
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public ILayoutNode Build()
    {
        SyncInputFromDraft();
        _input.OnFocused();
        return NetclawTuiChrome.BuildTextInputPanel(_input, _commit.Label);
    }

    public bool HandleInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            Commit(NetclawUiCommitTrigger.Enter);
            return true;
        }

        _input.HandleInput(keyInfo);
        StageInputText();
        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        ArgumentNullException.ThrowIfNull(paste);

        foreach (var ch in paste.Content)
        {
            if (ch is '\r' or '\n')
                continue;

            _input.HandleInput(ToKeyInfo(ch));
        }

        StageInputText();
    }

    public NetclawUiCommitResult Commit(NetclawUiCommitTrigger trigger)
    {
        StageInputText();
        LastCommitResult = _pipeline.CommitAsync(_commit, trigger)
            .GetAwaiter()
            .GetResult();
        return LastCommitResult;
    }

    private void SyncInputFromDraft()
    {
        var draft = _commit.ReadDraft();
        // Focused Termina inputs can be mutated directly by the input pipeline
        // (notably paste), so stage those edits instead of overwriting them.
        if (!StringComparer.Ordinal.Equals(_input.Text, _lastObservedDraft)
            && StringComparer.Ordinal.Equals(draft, _lastObservedDraft))
        {
            StageInputText();
            return;
        }

        if (StringComparer.Ordinal.Equals(draft, _lastObservedDraft))
            return;

        if (StringComparer.Ordinal.Equals(_input.Text, draft))
        {
            _lastObservedDraft = draft;
            return;
        }

        LastCommitResult = null;
        _input.Text = draft;
        _lastObservedDraft = draft;
        if (!string.IsNullOrEmpty(_input.Text))
            MoveCursorToEnd();
    }

    private void StageInputText()
    {
        LastCommitResult = null;
        _lastObservedDraft = _input.Text;
        _commit.WriteDraft(_lastObservedDraft);
    }

    private void MoveCursorToEnd()
        => _input.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));

    private static ConsoleKeyInfo ToKeyInfo(char ch)
        => new(ch, ToConsoleKey(ch), shift: char.IsUpper(ch), alt: false, control: false);

    private static ConsoleKey ToConsoleKey(char ch)
    {
        if (char.IsLetter(ch))
            return Enum.Parse<ConsoleKey>(char.ToUpperInvariant(ch).ToString());

        if (char.IsDigit(ch))
            return (ConsoleKey)((int)ConsoleKey.D0 + (ch - '0'));

        return ch switch
        {
            ' ' => ConsoleKey.Spacebar,
            '-' or '_' => ConsoleKey.OemMinus,
            '=' or '+' => ConsoleKey.OemPlus,
            '[' or '{' => ConsoleKey.Oem4,
            ']' or '}' => ConsoleKey.Oem6,
            '\\' or '|' => ConsoleKey.Oem5,
            ';' or ':' => ConsoleKey.Oem1,
            '\'' or '"' => ConsoleKey.Oem7,
            ',' or '<' => ConsoleKey.OemComma,
            '.' or '>' => ConsoleKey.OemPeriod,
            '/' or '?' => ConsoleKey.Oem2,
            '`' or '~' => ConsoleKey.Oem3,
            _ => (ConsoleKey)0,
        };
    }
}
