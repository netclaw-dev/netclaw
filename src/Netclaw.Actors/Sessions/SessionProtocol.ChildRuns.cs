// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.ChildRuns.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

public static partial class SessionProtocol
{
    public sealed record ChildRunAccepted : ISessionEvent
    {
        public required SessionId SessionId { get; init; }
        public required BackgroundChildRun Run { get; init; }
        public SerializableChatMessage? AcceptanceResult { get; init; }
        public required long RecordedAtMs { get; init; }
        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
    }

    public abstract record ChildRunEvent : ISessionEvent
    {
        public required SessionId SessionId { get; init; }
        public required SubAgentRunId RunId { get; init; }
        public required long RecordedAtMs { get; init; }
        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);

        public sealed record Checkpointed(ChildRunCheckpoint Checkpoint) : ChildRunEvent;
        public sealed record CancellationRequested : ChildRunEvent;
        public sealed record TerminalRecorded(ChildRunTerminal Terminal) : ChildRunEvent;
        public sealed record ResultPrepared(SerializableChatMessage Call, SerializableChatMessage Result, WorkingContextDelta? ReviewedWorkingContext) : ChildRunEvent;
    }

    /// <summary>Why the session closed the child approvals of a run.</summary>
    public enum ChildApprovalCloseReason
    {
        RunCancelled,
        RunTerminal,
        OwnerLost
    }

    /// <summary>
    /// Persisted when the session closes every approval entry of one child run (#2525). Replay applies it
    /// without a log entry. Applying it to a run with no entry is not an error.
    /// </summary>
    // Test-first contract. The proto message, the mapper, and manifest "cac-v1" are the implementer's work.
    public sealed record ChildApprovalsClosed : ISessionEvent
    {
        public required SessionId SessionId { get; init; }
        public required SubAgentRunId RunId { get; init; }
        public required ChildApprovalCloseReason Reason { get; init; }
        public required long ClosedAtMs { get; init; }
        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(ClosedAtMs);
    }
}
