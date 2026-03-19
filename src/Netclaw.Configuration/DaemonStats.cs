namespace Netclaw.Configuration;

/// <summary>
/// Wire types for the daemon usage statistics endpoint.
/// Nested types represent the JSON shape returned by the stats API.
/// </summary>
public static class DaemonStats
{
    public sealed class Response : IWireType
    {
        public required Process Process { get; init; }

        public required Tokens Tokens { get; init; }

        public required Sessions Sessions { get; init; }

        public required Memory Memory { get; init; }

        public required Skills Skills { get; init; }

        public required SlackActivity SlackActivity { get; init; }

        public Reminders? Reminders { get; init; }

        /// <summary>
        /// Daily stats breakdown. Empty when no days filter is specified.
        /// Contains trailing N-day rows when <c>?days=N</c> query parameter is used.
        /// </summary>
        public List<DailyRow> DailyBreakdown { get; init; } = [];
    }

    public sealed class Process : IWireType
    {
        public long UptimeSeconds { get; init; }

        public DateTimeOffset StartedAtUtc { get; init; }
    }

    public sealed class Tokens : IWireType
    {
        /// <summary>Cumulative input tokens this process lifetime.</summary>
        public long InputTokensTotal { get; init; }

        /// <summary>Cumulative output tokens this process lifetime.</summary>
        public long OutputTokensTotal { get; init; }

        /// <summary>Cumulative turns completed this process lifetime.</summary>
        public long TurnsCompletedTotal { get; init; }

        /// <summary>Cumulative memories formed this process lifetime.</summary>
        public long MemoriesFormedTotal { get; init; }

        /// <summary>Cumulative memories recalled this process lifetime.</summary>
        public long MemoriesRecalledTotal { get; init; }

        /// <summary>Cumulative skills auto-loaded this process lifetime.</summary>
        public long SkillsLoadedTotal { get; init; }
    }

    public sealed class Sessions : IWireType
    {
        public int TotalSessions { get; init; }

        public int ActiveSessions { get; init; }

        public long TotalTurns { get; init; }
    }

    public sealed class Memory : IWireType
    {
        public string Status { get; init; } = "unavailable";

        public int AnchorCount { get; init; }

        public int DocumentCount { get; init; }

        public int RecordCount { get; init; }

        public int EdgeCount { get; init; }

        public int PendingCheckpoints { get; init; }
    }

    public sealed class Skills : IWireType
    {
        public int TotalAvailable { get; init; }

        public int WithEnrichedKeywords { get; init; }
    }

    public sealed class SlackActivity : IWireType
    {
        public long EventsReceived { get; init; }

        public long EventsRouted { get; init; }

        public long EventsDropped { get; init; }

        public long RepliesPosted { get; init; }

        public long RepliesFailed { get; init; }

        public long RepliesPlainTextFallback { get; init; }
    }

    public sealed class Reminders : IWireType
    {
        /// <summary>Number of enabled reminder definitions currently scheduled.</summary>
        public int ScheduledCount { get; init; }

        /// <summary>Number of reminder executions currently in flight.</summary>
        public int ActiveExecutions { get; init; }

        /// <summary>Number of reminders that have recorded at least one consecutive failure.</summary>
        public int FailedCount { get; init; }
    }

    /// <summary>
    /// A single day's usage statistics from the <c>daily_stats</c> table.
    /// </summary>
    public sealed class DailyRow : IWireType
    {
        public required string Date { get; init; }

        public long InputTokens { get; init; }

        public long OutputTokens { get; init; }

        public long Turns { get; init; }

        public long Sessions { get; init; }

        public long MemoriesFormed { get; init; }

        public long MemoriesRecalled { get; init; }

        public long SkillsLoaded { get; init; }
    }
}
