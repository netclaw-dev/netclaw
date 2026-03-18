using Akka.Actor;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Buffers daily stat increments in memory and flushes to SQLite on a timer
/// and at shutdown. Replaces per-event SQLite writes with batched persistence.
/// </summary>
public sealed class DailyStatsActor : ReceiveActor, IWithTimers
{
    private const string FlushTimerKey = "flush";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyStatsActor> _logger;
    private readonly Dictionary<string, Accumulator> _pending = new();

    // Process-lifetime totals (never reset, never persisted — lost on restart)
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalTurns;
    private long _totalMemoriesFormed;
    private long _totalMemoriesRecalled;
    private long _totalSkillsLoaded;

    public ITimerScheduler Timers { get; set; } = null!;

    public DailyStatsActor(NetclawPaths paths, TimeProvider timeProvider, ILogger<DailyStatsActor> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _timeProvider = timeProvider;
        _logger = logger;

        Receive<RecordTokenUsage>(msg =>
        {
            GetOrCreate().InputTokens += msg.InputTokens;
            GetOrCreate().OutputTokens += msg.OutputTokens;
            _totalInputTokens += msg.InputTokens;
            _totalOutputTokens += msg.OutputTokens;
        });

        Receive<RecordTurnCompleted>(_ => { GetOrCreate().Turns++; _totalTurns++; });
        Receive<RecordSessionCreated>(_ => GetOrCreate().Sessions++);
        Receive<RecordMemoriesFormed>(msg =>
        {
            if (msg.Count > 0) { GetOrCreate().MemoriesFormed += msg.Count; _totalMemoriesFormed += msg.Count; }
        });
        Receive<RecordMemoriesRecalled>(msg =>
        {
            if (msg.Count > 0) { GetOrCreate().MemoriesRecalled += msg.Count; _totalMemoriesRecalled += msg.Count; }
        });
        Receive<RecordSkillsLoaded>(msg =>
        {
            if (msg.Count > 0) { GetOrCreate().SkillsLoaded += msg.Count; _totalSkillsLoaded += msg.Count; }
        });

        Receive<Flush>(_ => FlushToSqlite());

        Receive<QueryDailyStats>(msg =>
        {
            var rows = ReadFromSqlite(msg.Days);
            MergeUnflushed(rows);
            Sender.Tell(new QueryDailyStatsResult(rows));
        });

        Receive<QueryProcessStats>(_ => Sender.Tell(new ProcessStatsResult(
            _totalInputTokens, _totalOutputTokens, _totalTurns,
            _totalMemoriesFormed, _totalMemoriesRecalled, _totalSkillsLoaded)));
    }

    protected override void PreStart()
    {
        base.PreStart();
        EnsureTable();
        Timers.StartPeriodicTimer(FlushTimerKey, Flush.Instance, FlushInterval, FlushInterval);
    }

    protected override void PostStop()
    {
        FlushToSqlite();
        base.PostStop();
    }

    private Accumulator GetOrCreate()
    {
        var key = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        if (!_pending.TryGetValue(key, out var acc))
        {
            acc = new Accumulator();
            _pending[key] = acc;
        }
        return acc;
    }

    private void FlushToSqlite()
    {
        if (_pending.Count == 0)
            return;

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            foreach (var (dateKey, acc) in _pending)
            {
                if (acc.IsEmpty)
                    continue;

                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO daily_stats (date_key, input_tokens, output_tokens, turns, sessions,
                                             memories_formed, memories_recalled, skills_loaded)
                    VALUES ($date, $in, $out, $turns, $sessions, $formed, $recalled, $skills)
                    ON CONFLICT(date_key) DO UPDATE SET
                        input_tokens      = input_tokens      + $in,
                        output_tokens     = output_tokens     + $out,
                        turns             = turns             + $turns,
                        sessions          = sessions          + $sessions,
                        memories_formed   = memories_formed   + $formed,
                        memories_recalled = memories_recalled + $recalled,
                        skills_loaded     = skills_loaded     + $skills
                    """;
                cmd.Parameters.AddWithValue("$date", dateKey);
                cmd.Parameters.AddWithValue("$in", acc.InputTokens);
                cmd.Parameters.AddWithValue("$out", acc.OutputTokens);
                cmd.Parameters.AddWithValue("$turns", acc.Turns);
                cmd.Parameters.AddWithValue("$sessions", acc.Sessions);
                cmd.Parameters.AddWithValue("$formed", acc.MemoriesFormed);
                cmd.Parameters.AddWithValue("$recalled", acc.MemoriesRecalled);
                cmd.Parameters.AddWithValue("$skills", acc.SkillsLoaded);
                cmd.ExecuteNonQuery();
            }

            _pending.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush daily stats to SQLite");
        }
    }

    private List<DailyStatsRow> ReadFromSqlite(int days)
    {
        var rows = new List<DailyStatsRow>();

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            if (days > 0)
            {
                var startDate = _timeProvider.GetUtcNow().AddDays(-(days - 1)).ToString("yyyy-MM-dd");
                cmd.CommandText =
                    """
                    SELECT date_key, input_tokens, output_tokens, turns, sessions,
                           memories_formed, memories_recalled, skills_loaded
                    FROM daily_stats
                    WHERE date_key >= $start
                    ORDER BY date_key DESC
                    """;
                cmd.Parameters.AddWithValue("$start", startDate);
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT date_key, input_tokens, output_tokens, turns, sessions,
                           memories_formed, memories_recalled, skills_loaded
                    FROM daily_stats
                    ORDER BY date_key DESC
                    """;
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new DailyStatsRow(
                    DateKey: reader.GetString(0),
                    InputTokens: reader.GetInt64(1),
                    OutputTokens: reader.GetInt64(2),
                    Turns: reader.GetInt64(3),
                    Sessions: reader.GetInt64(4),
                    MemoriesFormed: reader.GetInt64(5),
                    MemoriesRecalled: reader.GetInt64(6),
                    SkillsLoaded: reader.GetInt64(7)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query daily stats");
        }

        return rows;
    }

    /// <summary>
    /// Merge any unflushed in-memory accumulators into the SQLite-read rows
    /// so the query response is always up-to-date.
    /// </summary>
    private void MergeUnflushed(List<DailyStatsRow> rows)
    {
        foreach (var (dateKey, acc) in _pending)
        {
            if (acc.IsEmpty)
                continue;

            var existing = rows.FindIndex(r => r.DateKey == dateKey);
            if (existing >= 0)
            {
                var r = rows[existing];
                rows[existing] = r with
                {
                    InputTokens = r.InputTokens + acc.InputTokens,
                    OutputTokens = r.OutputTokens + acc.OutputTokens,
                    Turns = r.Turns + acc.Turns,
                    Sessions = r.Sessions + acc.Sessions,
                    MemoriesFormed = r.MemoriesFormed + acc.MemoriesFormed,
                    MemoriesRecalled = r.MemoriesRecalled + acc.MemoriesRecalled,
                    SkillsLoaded = r.SkillsLoaded + acc.SkillsLoaded,
                };
            }
            else
            {
                rows.Add(new DailyStatsRow(
                    dateKey, acc.InputTokens, acc.OutputTokens, acc.Turns,
                    acc.Sessions, acc.MemoriesFormed, acc.MemoriesRecalled, acc.SkillsLoaded));
            }
        }

        rows.Sort((a, b) => string.Compare(b.DateKey, a.DateKey, StringComparison.Ordinal));
    }

    private void EnsureTable()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS daily_stats (
                    date_key          TEXT NOT NULL PRIMARY KEY,
                    input_tokens      INTEGER NOT NULL DEFAULT 0,
                    output_tokens     INTEGER NOT NULL DEFAULT 0,
                    turns             INTEGER NOT NULL DEFAULT 0,
                    sessions          INTEGER NOT NULL DEFAULT 0,
                    memories_formed   INTEGER NOT NULL DEFAULT 0,
                    memories_recalled INTEGER NOT NULL DEFAULT 0,
                    skills_loaded     INTEGER NOT NULL DEFAULT 0
                )
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure daily_stats table");
        }
    }

    // ── Messages ─────────────────────────────────────────────────────

    public sealed record RecordTokenUsage(long InputTokens, long OutputTokens);
    public sealed record RecordTurnCompleted;
    public sealed record RecordSessionCreated;
    public sealed record RecordMemoriesFormed(int Count);
    public sealed record RecordMemoriesRecalled(int Count);
    public sealed record RecordSkillsLoaded(int Count);
    public sealed record QueryDailyStats(int Days);
    public sealed record QueryDailyStatsResult(List<DailyStatsRow> Rows);
    public sealed record QueryProcessStats;
    public sealed record ProcessStatsResult(
        long InputTokensTotal, long OutputTokensTotal, long TurnsCompletedTotal,
        long MemoriesFormedTotal, long MemoriesRecalledTotal, long SkillsLoadedTotal);
    private sealed class Flush { public static readonly Flush Instance = new(); }

    // ── Types ────────────────────────────────────────────────────────

    public sealed record DailyStatsRow(
        string DateKey,
        long InputTokens,
        long OutputTokens,
        long Turns,
        long Sessions,
        long MemoriesFormed,
        long MemoriesRecalled,
        long SkillsLoaded);

    private sealed class Accumulator
    {
        public long InputTokens;
        public long OutputTokens;
        public long Turns;
        public long Sessions;
        public long MemoriesFormed;
        public long MemoriesRecalled;
        public long SkillsLoaded;

        public bool IsEmpty =>
            InputTokens == 0 && OutputTokens == 0 && Turns == 0 && Sessions == 0 &&
            MemoriesFormed == 0 && MemoriesRecalled == 0 && SkillsLoaded == 0;
    }
}
