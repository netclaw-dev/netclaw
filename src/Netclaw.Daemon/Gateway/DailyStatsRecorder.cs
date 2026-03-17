using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Records daily usage statistics into the <c>daily_stats</c> table.
/// Each method performs a single upsert keyed by the current UTC date.
/// </summary>
public sealed class DailyStatsRecorder
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyStatsRecorder> _logger;

    public DailyStatsRecorder(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<DailyStatsRecorder> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void RecordTokenUsage(long inputTokens, long outputTokens)
    {
        Upsert("input_tokens", inputTokens, "output_tokens", outputTokens);
    }

    public void RecordTurnCompleted()
    {
        Upsert("turns", 1);
    }

    public void RecordSessionCreated()
    {
        Upsert("sessions", 1);
    }

    public void RecordMemoriesFormed(int count)
    {
        if (count > 0)
            Upsert("memories_formed", count);
    }

    public void RecordMemoriesRecalled(int count)
    {
        if (count > 0)
            Upsert("memories_recalled", count);
    }

    public void RecordSkillsLoaded(int count)
    {
        if (count > 0)
            Upsert("skills_loaded", count);
    }

    public sealed record DailyStatsRow(
        string DateKey,
        long InputTokens,
        long OutputTokens,
        long Turns,
        long Sessions,
        long MemoriesFormed,
        long MemoriesRecalled,
        long SkillsLoaded);

    /// <summary>
    /// Query daily stats rows for a date range. If <paramref name="days"/> is 0 or negative,
    /// returns all rows (all-time). Otherwise returns the last N days.
    /// </summary>
    public List<DailyStatsRow> Query(int days)
    {
        var rows = new List<DailyStatsRow>();

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureTable(conn);

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

    private string TodayKey() => _timeProvider.GetUtcNow().ToString("yyyy-MM-dd");

    private void Upsert(string column, long value)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureTable(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO daily_stats (date_key, {column})
                VALUES ($date, $val)
                ON CONFLICT(date_key) DO UPDATE SET {column} = {column} + $val
                """;
            cmd.Parameters.AddWithValue("$date", TodayKey());
            cmd.Parameters.AddWithValue("$val", value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record daily stats for {Column}", column);
        }
    }

    private void Upsert(string column1, long value1, string column2, long value2)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureTable(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO daily_stats (date_key, {column1}, {column2})
                VALUES ($date, $val1, $val2)
                ON CONFLICT(date_key) DO UPDATE SET
                    {column1} = {column1} + $val1,
                    {column2} = {column2} + $val2
                """;
            cmd.Parameters.AddWithValue("$date", TodayKey());
            cmd.Parameters.AddWithValue("$val1", value1);
            cmd.Parameters.AddWithValue("$val2", value2);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record daily stats for {Column1}/{Column2}", column1, column2);
        }
    }

    private static void EnsureTable(SqliteConnection conn)
    {
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
}
