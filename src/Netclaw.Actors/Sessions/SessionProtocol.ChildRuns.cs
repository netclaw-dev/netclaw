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
        public sealed record ResultPrepared(SerializableChatMessage Call, SerializableChatMessage Result) : ChildRunEvent;
    }
}
