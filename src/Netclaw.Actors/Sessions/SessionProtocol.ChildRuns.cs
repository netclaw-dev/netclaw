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

        // durable-child-park-recovery: the park with its requests, and the resume before any parked call runs.
        internal sealed record Parked(ChildRunPark Park, IReadOnlyList<ToolApprovalRequested> Requests)
            : ChildRunEvent;

        internal sealed record Resumed : ChildRunEvent;

        /// <summary>
        /// Read-only. A deployed runtime wrote this event for the old tool-pair delivery. The reader keeps the pair
        /// until a legacy closure puts it into the history. No writer creates this event.
        /// </summary>
        public sealed record ResultPrepared(SerializableChatMessage Call, SerializableChatMessage Result, WorkingContextDelta? ReviewedWorkingContext) : ChildRunEvent;
    }

    /// <summary>
    /// The kind of an admitted input that reports the end of a child run to its parent. The input text names the run
    /// and its <c>result.md</c> file and never holds the output. The input applies the working context that the owner
    /// reviewed for the run, in the same step that marks the run as reported.
    /// </summary>
    public sealed record ChildNotice(SubAgentRunId RunId, WorkingContextDelta? ReviewedWorkingContext);
}
