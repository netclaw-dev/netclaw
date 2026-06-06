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
    ILayoutNode Build();

    bool HandleInput(ConsoleKeyInfo keyInfo);

    void HandlePaste(PasteEvent paste);
}

internal sealed class NetclawValidatedTextField : INetclawUiComponent
{
    private readonly NetclawUiCommit<string> _commit;
    private readonly NetclawUiCommitPipeline _pipeline;
    private readonly TextInputNode _input;

    public NetclawValidatedTextField(
        NetclawUiCommit<string> commit,
        NetclawUiCommitPipeline pipeline,
        TextInputNode input)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _input.Text = commit.ReadDraft();
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public ILayoutNode Build()
    {
        _input.OnFocused();
        return NetclawTuiChrome.BuildTextInputPanel(_input, _commit.Label);
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

        _input.HandleInput(keyInfo);
        _commit.WriteDraft(_input.Text);
        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _input.HandlePaste(paste);
        _commit.WriteDraft(_input.Text);
    }
}
