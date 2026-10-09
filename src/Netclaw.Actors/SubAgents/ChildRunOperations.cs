// -----------------------------------------------------------------------
// <copyright file="ChildRunOperations.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

/// <summary>Live preparation stays inside the existing owner adapter.</summary>
internal sealed record PreparedChildRun : INoSerializationVerificationNeeded
{
    public required Props Props { get; init; }
    public required SubAgentRunId RunId { get; init; }
    public required AgentName AgentName { get; init; }
    public required int ToolCount { get; init; }
    public required string ArgumentsDigest { get; init; }
    public required RunSubAgent Execution { get; init; }
}

/// <summary>The pipeline stamps these facts before asynchronous tool setup.</summary>
internal sealed record StartBackgroundChildRun(
    PreparedChildRun Prepared,
    ChildRunStartKey StartKey,
    TurnContextRecord InvocationContext,
    string SourceOperation,
    CancellationToken ExecutionToken) : INoSerializationVerificationNeeded;

internal sealed record RunBackgroundSubAgent(SubAgentRunId RunId, RunSubAgent Execution, ChildRunDispatch Dispatch) : INoSerializationVerificationNeeded;
internal sealed record BackgroundChildTerminal(SubAgentRunId RunId, SubAgentResult Result) : INoSerializationVerificationNeeded;
internal sealed record BackgroundChildTerminalAck(SubAgentRunId RunId) : INoSerializationVerificationNeeded;

internal abstract record ChildStartReply : INoSerializationVerificationNeeded
{
    internal sealed record Accepted(BackgroundChildRun Run, BackgroundChildState State) : ChildStartReply
    {
        internal void Validate(PreparedChildRun prepared)
        {
            Run.Validate();
            // A duplicate can retain another run ID and a later state, but its preparation must match.
            if (Run.ArgumentsDigest != prepared.ArgumentsDigest || Run.AgentName != prepared.AgentName || State != Run.State)
                throw new InvalidDataException("The child acceptance differs from its canonical preparation or current state.");
        }

        internal void Validate(StartBackgroundChildRun request)
        {
            Validate(request.Prepared);
            if (Run.StartKey != request.StartKey
                || !SessionState.SameCanonicalContext(Run.OriginalContext, request.InvocationContext)
                || Run.SourceOperation != request.SourceOperation)
                throw new InvalidDataException("The child acceptance differs from its original start request.");
        }
    }
    internal sealed record Conflict : ChildStartReply;
}

internal sealed record BackgroundChildCheckpoint(SubAgentRunId RunId, ChildRunCheckpoint Checkpoint) : INoSerializationVerificationNeeded;
internal sealed record BackgroundChildCheckpointAck(SubAgentRunId RunId, int CompletedRound) : INoSerializationVerificationNeeded;

/// <summary>Closes local dispatch admission. It does not schedule provider capacity.</summary>
internal sealed class ChildRunDispatch
{
    private readonly object _sync = new();
    private bool _closed;

    public Task<T> Enter<T>(Func<Task<T>> operation)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _closed))
                throw new OperationCanceledException("The child dispatch admission is closed.");
            return operation();
        }
    }

    public Task Enter(Func<Task> operation)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _closed))
                throw new OperationCanceledException("The child dispatch admission is closed.");
            return operation();
        }
    }

    public void Close()
        => Volatile.Write(ref _closed, true);

    public Task WaitForEnteredInvocationAsync()
    {
        // Only the synchronous invocation holds this lock, not its returned remote task.
        return Task.Run(() =>
        {
            lock (_sync)
            {
                if (!Volatile.Read(ref _closed))
                    throw new InvalidOperationException("Child admission must close before its acknowledgement.");
            }
        });
    }

    internal static async Task CancelAndDisposeAsync(CancellationTokenSource source, ILoggingAdapter log)
    {
        try
        {
            // CancelAsync marks the token before arbitrary callbacks run outside the actor thread.
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            log.Error(error, "Child cancellation callbacks failed.");
        }
        finally
        {
            source.Dispose();
        }
    }
}

internal sealed record ChildControlRequest(SubAgentRunId RunId, bool Cancel) : INoSerializationVerificationNeeded;
internal sealed record ControlBackgroundChildRun(ChildControlRequest Request, TurnContextRecord InvocationContext,
    CancellationToken ExecutionToken) : INoSerializationVerificationNeeded;
internal sealed record ChildControlReply(BackgroundChildRun? Run) : INoSerializationVerificationNeeded;
