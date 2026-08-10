// -----------------------------------------------------------------------
// <copyright file="DailyStatsActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    private readonly Dictionary<string, Accumulator> _pending = [];
    private readonly Dictionary<(string DateKey, string SkillName, SkillLoadMethod Method), long> _pendingSkillUsage = [];

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
        Receive<RecordSkillLoaded>(msg =>
        {
            var normalizedSkill = msg.SkillName.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkill))
                return;

            GetOrCreate().SkillsLoaded++;
            _totalSkillsLoaded++;

            var key = (_timeProvider.GetUtcNow().ToString("yyyy-MM-dd"), normalizedSkill, msg.Method);
            _pendingSkillUsage[key] = _pendingSkillUsage.TryGetValue(key, out var existing)
                ? existing + 1
                : 1;
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
        Receive<QuerySkillUsageStats>(msg =>
        {
            var rows = ReadSkillUsageFromSqlite(msg.Days);
            MergeUnflushedSkillUsage(rows);
            Sender.Tell(new QuerySkillUsageStatsResult(rows));
        });
    }

    protected override void PreStart()
    {
        // Schema DDL is initialized synchronously at daemon startup by
        // SchemaMigrationHostedService — see DailyStatsSchema.EnsureAsync.
        // Keeping it off the actor's mailbox prevents Windows cold-start
        // (AV scan + first-time fsync) from delaying the first Ask.
        base.PreStart();
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
        if (_pending.Count == 0 && _pendingSkillUsage.Count == 0)
            return;

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();

            foreach (var (dateKey, acc) in _pending)
            {
                if (acc.IsEmpty)
                    continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
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

            foreach (var ((dateKey, skillName, method), count) in _pendingSkillUsage)
            {
                if (count <= 0)
                    continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO daily_skill_usage (date_key, skill_name, load_method, count)
                    VALUES ($date, $skill, $method, $count)
                    ON CONFLICT(date_key, skill_name, load_method) DO UPDATE SET
                        count = count + $count
                    """;
                cmd.Parameters.AddWithValue("$date", dateKey);
                cmd.Parameters.AddWithValue("$skill", skillName);
                cmd.Parameters.AddWithValue("$method", method.ToWireValue());
                cmd.Parameters.AddWithValue("$count", count);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();

            _pending.Clear();
            _pendingSkillUsage.Clear();
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

    private List<DailySkillUsageRow> ReadSkillUsageFromSqlite(int days)
    {
        var rows = new List<DailySkillUsageRow>();

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
                    SELECT date_key, skill_name, load_method, count
                    FROM daily_skill_usage
                    WHERE date_key >= $start
                    ORDER BY date_key DESC, count DESC, skill_name ASC, load_method ASC
                    """;
                cmd.Parameters.AddWithValue("$start", startDate);
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT date_key, skill_name, load_method, count
                    FROM daily_skill_usage
                    ORDER BY date_key DESC, count DESC, skill_name ASC, load_method ASC
                    """;
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!SkillLoadMethodExtensions.TryFromWireValue(reader.GetString(2), out var method))
                    continue;

                rows.Add(new DailySkillUsageRow(
                    DateKey: reader.GetString(0),
                    SkillName: reader.GetString(1),
                    Method: method,
                    Count: reader.GetInt64(3)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query daily skill usage stats");
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

    private void MergeUnflushedSkillUsage(List<DailySkillUsageRow> rows)
    {
        foreach (var ((dateKey, skillName, method), count) in _pendingSkillUsage)
        {
            if (count <= 0)
                continue;

            var existing = rows.FindIndex(r => r.DateKey == dateKey
                && string.Equals(r.SkillName, skillName, StringComparison.Ordinal)
                && r.Method == method);

            if (existing >= 0)
            {
                var row = rows[existing];
                rows[existing] = row with { Count = row.Count + count };
            }
            else
            {
                rows.Add(new DailySkillUsageRow(dateKey, skillName, method, count));
            }
        }

        rows.Sort((a, b) =>
        {
            var dateCompare = string.Compare(b.DateKey, a.DateKey, StringComparison.Ordinal);
            if (dateCompare != 0)
                return dateCompare;

            var countCompare = b.Count.CompareTo(a.Count);
            if (countCompare != 0)
                return countCompare;

            var skillCompare = string.Compare(a.SkillName, b.SkillName, StringComparison.Ordinal);
            if (skillCompare != 0)
                return skillCompare;

            return string.Compare(a.Method.ToWireValue(), b.Method.ToWireValue(), StringComparison.Ordinal);
        });
    }

    // ── Messages ─────────────────────────────────────────────────────

    public sealed record RecordTokenUsage(long InputTokens, long OutputTokens);
    public sealed record RecordTurnCompleted;
    public sealed record RecordSessionCreated;
    public sealed record RecordMemoriesFormed(int Count);
    public sealed record RecordMemoriesRecalled(int Count);
    public sealed record RecordSkillsLoaded(int Count);
    public sealed record RecordSkillLoaded(string SkillName, SkillLoadMethod Method);
    public sealed record QueryDailyStats(int Days);
    public sealed record QueryDailyStatsResult(List<DailyStatsRow> Rows);
    public sealed record QuerySkillUsageStats(int Days);
    public sealed record QuerySkillUsageStatsResult(List<DailySkillUsageRow> Rows);
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

    public sealed record DailySkillUsageRow(
        string DateKey,
        string SkillName,
        SkillLoadMethod Method,
        long Count);

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
