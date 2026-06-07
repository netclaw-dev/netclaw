// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedAction.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui;

internal class NetclawValidatedAction<TDraft>
{
    private readonly NetclawUiCommit<TDraft> _commit;
    private readonly NetclawUiCommitPipeline _pipeline;
    private readonly NetclawUiCommitTrigger _trigger;

    public NetclawValidatedAction(
        NetclawUiCommit<TDraft> commit,
        NetclawUiCommitPipeline pipeline,
        NetclawUiCommitTrigger trigger)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _trigger = trigger;
    }

    public NetclawUiCommitResult? LastCommitResult { get; private set; }

    public NetclawUiCommitResult Invoke()
    {
        LastCommitResult = _pipeline.CommitAsync(_commit, _trigger)
            .GetAwaiter()
            .GetResult();
        return LastCommitResult;
    }
}

internal sealed class NetclawValidatedToggle<TDraft> : NetclawValidatedAction<TDraft>
{
    public NetclawValidatedToggle(NetclawUiCommit<TDraft> commit, NetclawUiCommitPipeline pipeline)
        : base(commit, pipeline, NetclawUiCommitTrigger.Toggle)
    {
    }
}
