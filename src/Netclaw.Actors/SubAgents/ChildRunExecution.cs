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
    AgentName AgentName, string ArgumentsDigest, SessionStoragePaths Storage, int ToolCount, RunSubAgent Execution);
internal sealed record AcceptChildStart(PreparedChildStart Prepared, ChildRunStartKey StartKey,
    TurnContextRecord OriginalContext, string SourceOperation, CancellationToken Cancellation)
    : INoSerializationVerificationNeeded;
internal sealed record ChildTerminalReturned(SubAgentRunId RunId, SubAgentResult Result)
    : INoSerializationVerificationNeeded;
internal sealed record ChildTerminalCommitted : INoSerializationVerificationNeeded;
internal sealed record ChildCheckpointReached(SubAgentRunId RunId, ChildRunCheckpoint Checkpoint, IActorRef Child)
    : INoSerializationVerificationNeeded;
internal sealed record ChildCheckpointCommitted(bool ForceNoTools) : INoSerializationVerificationNeeded;
internal sealed record ChildResultPrepared(SubAgentRunId RunId, ChildRunTerminal ExpectedTerminal,
    SerializableChatMessage Call, SerializableChatMessage Result, WorkingContextDelta? ReviewedWorkingContext) : INoSerializationVerificationNeeded;
internal sealed record ChildPreparationFailed(SubAgentRunId RunId, ChildRunTerminal ExpectedTerminal, Exception Error)
    : INoSerializationVerificationNeeded;
internal sealed record ChildStopFailed(SubAgentRunId RunId, Exception Error) : INoSerializationVerificationNeeded;
internal sealed record RecoverChildRuns : INoSerializationVerificationNeeded;
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
