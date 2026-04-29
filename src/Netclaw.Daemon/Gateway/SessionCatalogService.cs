// -----------------------------------------------------------------------
// <copyright file="SessionCatalogService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Singleton service maintaining the <c>sessions</c> table in the SQLite database.
/// Implements <see cref="ISessionLifecycleObserver"/> so session activation,
/// deactivation, and output events update the SQLite-backed catalog.
///
/// Per-session log file I/O is handled by <see cref="Netclaw.Actors.Sessions.SessionLogActor"/>
/// (a child of each session actor), not this service.
/// </summary>
public sealed class SessionCatalogService : ISessionLifecycleObserver
{
    private enum SessionsSchemaMode
    {
        Missing,
        Legacy,
        Current
    }

    private readonly string _connectionString;
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionCatalogService> _logger;
    private readonly ISessionMetrics? _metrics;

    public SessionCatalogService(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<SessionCatalogService> logger,
        ISessionMetrics? metrics = null)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _timeProvider = timeProvider;
        _logger = logger;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public void OnSessionActivated(SessionId sessionId, Actors.Channels.ChannelType channelType)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var persistenceId = $"session-{sessionId.Value}";

        try
        {
            var logPath = SessionLogActor.GetSessionLogsDirectory(sessionId, _paths.SessionLogsDirectory);

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count, log_path)
                VALUES ($pid, $channel, $now, $now, 'active', 0, $path)
                ON CONFLICT(persistence_id) DO UPDATE SET
                    status = 'active',
                    log_path = $path
                """;
            cmd.Parameters.AddWithValue("$pid", persistenceId);
            cmd.Parameters.AddWithValue("$channel", channelType.ToWireValue());
            cmd.Parameters.AddWithValue("$path", logPath);
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.ExecuteNonQuery();

            _metrics?.RecordSessionCreated();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark session {SessionId} active", sessionId.Value);
        }
    }

    /// <inheritdoc />
    public void OnSessionDeactivated(SessionId sessionId)
    {
        var persistenceId = $"session-{sessionId.Value}";

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE sessions SET
                    status = 'inactive'
                WHERE persistence_id = $pid
                """;
            cmd.Parameters.AddWithValue("$pid", persistenceId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to mark session {SessionId} inactive", sessionId.Value);
        }
    }

    /// <inheritdoc />
    public void OnOutput(SessionOutput output)
    {
        var persistenceId = $"session-{output.SessionId.Value}";

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            switch (output)
            {
                case TurnCompleted tc when tc.Outcome != TurnOutcome.Skipped:
                    UpdateSession(conn, persistenceId, cmd =>
                    {
                        cmd.CommandText =
                            """
                            UPDATE sessions SET
                                turn_count = turn_count + 1,
                                last_activity = $now
                            WHERE persistence_id = $pid
                            """;
                    });
                    _metrics?.RecordTurnCompleted();
                    break;

                case TurnCompleted:
                    UpdateLastActivity(conn, persistenceId);
                    break;

                case UsageOutput usage
                    when usage.InputTokens.HasValue:
                    UpdateSession(conn, persistenceId, cmd =>
                    {
                        cmd.CommandText =
                            """
                            UPDATE sessions SET
                                last_input_tokens = $tokens,
                                last_activity = $now
                            WHERE persistence_id = $pid
                            """;
                        cmd.Parameters.AddWithValue("$tokens", usage.InputTokens.Value);
                    });
                    break;

                case SessionTitleOutput title:
                    UpdateSession(conn, persistenceId, cmd =>
                    {
                        cmd.CommandText =
                            """
                            UPDATE sessions SET
                                title = $title,
                                last_activity = $now
                            WHERE persistence_id = $pid
                            """;
                        cmd.Parameters.AddWithValue("$title", title.Title);
                    });
                    break;

                case CompactionOutput:
                case ErrorOutput:
                    UpdateLastActivity(conn, persistenceId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle output for session {SessionId}", output.SessionId.Value);
        }
    }

    public sealed record SessionStats(
        int TotalSessions,
        int ActiveSessions,
        long TotalTurns);

    public SessionStats GetStats()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN status = 'active' THEN 1 ELSE 0 END),
                    SUM(turn_count)
                FROM sessions
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new SessionStats(
                    TotalSessions: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    ActiveSessions: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    TotalTurns: reader.IsDBNull(2) ? 0 : reader.GetInt64(2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get session stats");
        }

        return new SessionStats(0, 0, 0);
    }

    /// <summary>
    /// List recent sessions, ordered by last activity descending.
    /// </summary>
    public List<SessionCatalogEntry> ListRecent(int limit = 50)
    {
        var entries = new List<SessionCatalogEntry>();

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT persistence_id, channel, title, description, status, turn_count,
                       created_at, last_activity, log_path, last_input_tokens
                FROM sessions
                ORDER BY last_activity DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new SessionCatalogEntry
                {
                    PersistenceId = reader.GetString(0),
                    Channel = reader.GetString(1),
                    Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Status = NormalizeStatus(reader.GetString(4)),
                    TurnCount = reader.GetInt32(5),
                    CreatedAt = reader.GetInt64(6),
                    LastActivity = reader.GetInt64(7),
                    LogPath = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LastInputTokens = reader.IsDBNull(9) ? null : reader.GetInt64(9)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list sessions");
        }

        return entries;
    }

    /// <summary>
    /// Marks all 'active' sessions as 'inactive'. Called during daemon startup
    /// before any sessions are materialized, to clean up stale state from
    /// unclean shutdowns (crash, kill -9, power loss).
    /// </summary>
    public int ReconcileStaleActiveSessions()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET status = 'inactive' WHERE status = 'active'";
            var affected = cmd.ExecuteNonQuery();

            if (affected > 0)
                _logger.LogInformation(
                    "Reconciled {Count} stale active session(s) from previous run.", affected);

            return affected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reconcile stale active sessions during startup.");
            return 0;
        }
    }

    public void MarkSessionActive(SessionId sessionId)
    {
        var persistenceId = $"session-{sessionId.Value}";

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            EnsureSchemaUpToDate(conn, _logger);

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE sessions SET status = 'active' WHERE persistence_id = $pid";
            cmd.Parameters.AddWithValue("$pid", persistenceId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark session {SessionId} active during restart recovery", sessionId.Value);
        }
    }

    private void UpdateSession(
        SqliteConnection conn,
        string persistenceId,
        Action<SqliteCommand> configure)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        using var cmd = conn.CreateCommand();
        configure(cmd);
        cmd.Parameters.AddWithValue("$pid", persistenceId);
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.ExecuteNonQuery();
    }

    private void UpdateLastActivity(SqliteConnection conn, string persistenceId)
    {
        UpdateSession(conn, persistenceId, cmd =>
        {
            cmd.CommandText = "UPDATE sessions SET last_activity = $now WHERE persistence_id = $pid";
        });
    }

    private static string NormalizeStatus(string status)
        => string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase)
            ? "inactive"
            : status;

    // Shared DDL for the current sessions schema (used in both Missing and Legacy migration paths).
    private const string SessionsCreateTableDdl =
        """
        CREATE TABLE IF NOT EXISTS sessions (
            persistence_id    TEXT NOT NULL PRIMARY KEY,
            channel           TEXT NOT NULL,
            created_at        INTEGER NOT NULL,
            last_activity     INTEGER NOT NULL,
            status            TEXT NOT NULL DEFAULT 'active',
            turn_count        INTEGER NOT NULL DEFAULT 0,
            title             TEXT,
            description       TEXT,
            last_input_tokens INTEGER,
            log_path          TEXT,
            metadata          TEXT
        )
        """;

    /// <summary>
    /// Ensures the sessions table exists and is on the current schema.
    /// If the table is missing, it is created. If it uses the legacy schema
    /// (session_id column), the data is migrated to the current schema.
    /// This is a no-op when the table already uses the current schema.
    /// </summary>
    private static void EnsureSchemaUpToDate(SqliteConnection conn, ILogger logger)
    {
        var mode = DetectSchemaMode(conn);

        switch (mode)
        {
            case SessionsSchemaMode.Current:
                return;

            case SessionsSchemaMode.Missing:
                logger.LogInformation("Sessions table not found — creating with current schema");
                RunSql(conn, SessionsCreateTableDdl);
                RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions (status)");
                RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_last_activity ON sessions (last_activity)");
                break;

            case SessionsSchemaMode.Legacy:
                logger.LogInformation("Legacy sessions schema detected — migrating to current schema");
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        RunSql(conn, SessionsCreateTableDdl.Replace("sessions", "sessions_new", StringComparison.Ordinal));
                        RunSql(conn,
                            """
                            INSERT INTO sessions_new (persistence_id, channel, created_at, last_activity, status, turn_count, title)
                            SELECT
                                'session-' || session_id,
                                CASE
                                    WHEN session_id LIKE 'signalr/%' THEN 'signalr'
                                    WHEN session_id LIKE 'headless/%' THEN 'headless'
                                    WHEN session_id LIKE 'console/%'  THEN 'console'
                                    WHEN session_id LIKE 'C%' OR session_id LIKE 'D%' OR session_id LIKE 'G%' THEN 'slack'
                                    ELSE 'unknown'
                                END,
                                COALESCE(created, last_activity, 0),
                                COALESCE(last_activity, 0),
                                'active',
                                COALESCE(message_count, 0),
                                display_name
                            FROM sessions
                            """);
                        RunSql(conn, "DROP TABLE sessions");
                        RunSql(conn, "ALTER TABLE sessions_new RENAME TO sessions");
                        RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions (status)");
                        RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_last_activity ON sessions (last_activity)");
                        tx.Commit();
                        logger.LogInformation("Sessions schema migration complete");
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
                break;
        }
    }

    private static void RunSql(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SessionsSchemaMode DetectSchemaMode(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(sessions)";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
                columns.Add(reader.GetString(1));
        }

        if (columns.Count == 0)
            return SessionsSchemaMode.Missing;

        if (columns.Contains("persistence_id"))
            return SessionsSchemaMode.Current;

        if (columns.Contains("session_id"))
            return SessionsSchemaMode.Legacy;

        return SessionsSchemaMode.Missing;
    }
}

/// <summary>
/// DTO for session catalog entries returned by <c>GET /api/sessions</c>.
/// </summary>
public sealed class SessionCatalogEntry
{
    public required string PersistenceId { get; init; }
    public required string Channel { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required int TurnCount { get; init; }
    public required long CreatedAt { get; init; }
    public required long LastActivity { get; init; }
    public string? LogPath { get; init; }
    public long? LastInputTokens { get; init; }
}
