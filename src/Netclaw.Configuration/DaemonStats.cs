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

        public List<ChannelActivity> Channels { get; init; } = [];

        public required Webhooks Webhooks { get; init; }

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
    }

    public sealed class ChannelActivity : IWireType
    {
        public required string ChannelType { get; init; }

        public required string DisplayName { get; init; }

        public long EventsReceived { get; init; }

        public long EventsRouted { get; init; }

        public long EventsDropped { get; init; }

        public long RepliesPosted { get; init; }

        public long RepliesRejected { get; init; }

        public long RepliesFailed { get; init; }

        public Dictionary<string, long>? Extras { get; init; }
    }

    public sealed class Webhooks : IWireType
    {
        /// <summary>Total webhook route definition files present on disk.</summary>
        public int TotalRoutes { get; init; }

        /// <summary>Routes currently loaded and serving traffic.</summary>
        public int EnabledRoutes { get; init; }

        /// <summary>Routes whose file has <c>Enabled=false</c> (parsed but disabled).</summary>
        public int DisabledRoutes { get; init; }

        /// <summary>Routes whose file failed to parse or validate.</summary>
        public int InvalidRoutes { get; init; }

        /// <summary>Deliveries accepted and dispatched to a webhook session.</summary>
        public long Accepted { get; init; }

        /// <summary>Requests for unknown routes (HTTP 404).</summary>
        public long RouteNotFound { get; init; }

        /// <summary>Requests with invalid HMAC signature or header secret (HTTP 401).</summary>
        public long VerificationFailed { get; init; }

        /// <summary>Requests exceeding the route's <c>MaxBodyBytes</c> (HTTP 413).</summary>
        public long BodyTooLarge { get; init; }

        /// <summary>Requests with an unparseable JSON body (HTTP 400).</summary>
        public long InvalidJson { get; init; }

        /// <summary>Requests rejected by the per-route rate limiter (HTTP 429).</summary>
        public long RateLimited { get; init; }

        /// <summary>Deliveries filtered out because their event type is not allowed by the route.</summary>
        public long EventFiltered { get; init; }

        /// <summary>Deliveries ignored because their delivery identifier was seen recently.</summary>
        public long DuplicateDelivery { get; init; }
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
