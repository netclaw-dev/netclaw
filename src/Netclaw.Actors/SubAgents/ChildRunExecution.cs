// -----------------------------------------------------------------------
// <copyright file="ChildRunExecution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

internal sealed record PreparedChildStart(Props Props, string ActorName, SubAgentRunId RunId,
    AgentName AgentName, string ArgumentsDigest, SessionStoragePaths Storage, int ToolCount, RunSubAgent Execution)
{
    private ChildRunFiles Files => Storage.ChildFiles
        ?? throw new InvalidOperationException("A child start requires child run storage.");

    /// <summary>The folder that the spawner removes when the owner rejects the start.</summary>
    public string RunDirectory => Files.RunDirectory;

    /// <summary>
    /// Writes <c>task.md</c> for the requester that the start carries. The caller runs this before the owner accepts
    /// the start, so the file exists when the child makes its first model call.
    /// </summary>
    public async Task WriteTaskFileAsync(TurnContextRecord requester, CancellationToken cancellationToken)
    {
        try
        {
            await ChildRunDocuments.WriteAtomicAsync(Files.TaskFile,
                ChildRunDocuments.RenderTask(AgentName, requester, Execution.Task, Execution.RuntimeContext), cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ChildStartRejectedException(SubAgentOutcomeReason.SpawnError,
                $"The child was not started. The task file could not be written: {error.Message}");
        }
    }
}
internal sealed record AcceptChildStart(PreparedChildStart Prepared, ChildRunStartKey StartKey,
    TurnContextRecord OriginalContext, string SourceOperation, CancellationToken Cancellation)
    : INoSerializationVerificationNeeded;
internal sealed record ChildTerminalReturned(SubAgentRunId RunId, SubAgentResult Result)
    : INoSerializationVerificationNeeded;
internal sealed record ChildTerminalCommitted : INoSerializationVerificationNeeded;
internal sealed record ChildCheckpointReached(SubAgentRunId RunId, ChildRunCheckpoint Checkpoint, IActorRef Child)
    : INoSerializationVerificationNeeded;
internal sealed record ChildCheckpointCommitted : INoSerializationVerificationNeeded;
/// <summary>
/// The off-actor preparation of a result ended. The result file is written, or the write failed. The owner then admits
/// the notice. <paramref name="ReviewedWorkingContext"/> is the child context that passed the review gate.
/// </summary>
internal sealed record ChildResultWritten(SubAgentRunId RunId, WorkingContextDelta? ReviewedWorkingContext,
    string ResultPath, Exception? WriteFailure) : INoSerializationVerificationNeeded;
internal sealed record ChildStopFailed(SubAgentRunId RunId, Exception Error) : INoSerializationVerificationNeeded;
/// <summary>
/// The action that recovery chose for each child run when it completed. A stashed command runs before this message, so
/// a click can resume a parked run first; that run keeps the action that recovery chose for it.
/// </summary>
internal sealed record RecoverChildRuns(IReadOnlyList<(SubAgentRunId RunId, ChildRecoveryAction Action)> Actions)
    : INoSerializationVerificationNeeded;
internal sealed record ChildRunControl(SubAgentRunId RunId, bool Cancel, TurnContextRecord Requester)
    : INoSerializationVerificationNeeded;
internal sealed record ChildRunControlRequest(SubAgentRunId RunId, bool Cancel);

public sealed record ChildRunAcceptance(SubAgentRunId RunId, SubAgentScopeId ScopeId)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        run_id = RunId.Value, scope_id = ScopeId.Value, state = "accepted", control_tool = "check_agent_run"
    });
}

/// <summary>
/// The child start is refused before any child runs: the spawner or the owning session says no. Nothing in the
/// session journal names the start, so the spawner removes the run folder that it prepared.
/// </summary>
internal sealed class ChildStartRejectedException(SubAgentOutcomeReason reason, string message)
    : InvalidOperationException(message)
{
    public SubAgentOutcomeReason Reason { get; } = reason;
}

/// <summary>Closes new child dispatch before cancellation acknowledges the durable intent.</summary>
internal sealed class ChildRunExecution(SubAgentRunId runId, IActorRef owner) : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _closed;
    private Task? _stopTask;
    public SubAgentRunId RunId { get; } = runId;
    public IActorRef Owner { get; } = owner;
    public CancellationToken Token => _cancellation.Token;

    public Task<T> Enter<T>(Func<Task<T>> operation)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _closed))
                return Task.FromCanceled<T>(new CancellationToken(canceled: true));
            return operation();
        }
    }

    public void Close()
    {
        lock (_sync)
            _closed = true;
    }
    public Task StopAsync()
    {
        lock (_sync)
        {
            _closed = true;
            return _stopTask ??= _cancellation.CancelAsync();
        }
    }
    public void Dispose() => _cancellation.Dispose();
}
