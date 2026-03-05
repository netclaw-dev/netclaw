using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Singleton service maintaining the <c>sessions</c> table in the SQLite database.
/// Implements <see cref="ISessionLifecycleObserver"/> so <see cref="SessionPipeline"/>
/// automatically reports all session events regardless of channel type.
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

    public SessionCatalogService(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<SessionCatalogService> logger)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnSessionCreated(SessionId sessionId, string channelType)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var persistenceId = $"session-{sessionId.Value}";

        try
        {
            // Compute expected log path deterministically — the child SessionLogActor
            // independently creates the file at this same path.
            var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId.Value);
            var logPath = Path.Combine(_paths.SessionLogsDirectory, $"{sanitized}.log");

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            var schemaMode = DetectSchemaMode(conn);
            if (schemaMode is SessionsSchemaMode.Missing)
                return;

            using var cmd = conn.CreateCommand();
            if (schemaMode is SessionsSchemaMode.Current)
            {
                cmd.CommandText =
                    """
                    INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count, log_path)
                    VALUES ($pid, $channel, $now, $now, 'active', 0, $path)
                    ON CONFLICT(persistence_id) DO UPDATE SET
                        status = 'active',
                        last_activity = $now,
                        log_path = $path
                    """;
                cmd.Parameters.AddWithValue("$pid", persistenceId);
                cmd.Parameters.AddWithValue("$channel", channelType);
                cmd.Parameters.AddWithValue("$path", logPath);
            }
            else
            {
                cmd.CommandText =
                    """
                    INSERT INTO sessions (session_id, last_activity, message_count, created, display_name)
                    VALUES ($sid, $now, 0, $now, 'Session')
                    ON CONFLICT(session_id) DO UPDATE SET
                        last_activity = $now
                    """;
                cmd.Parameters.AddWithValue("$sid", sessionId.Value);
            }

            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record session creation for {SessionId}", sessionId.Value);
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

            var schemaMode = DetectSchemaMode(conn);
            if (schemaMode is SessionsSchemaMode.Missing)
                return;

            switch (output)
            {
                case TurnCompleted:
                    if (schemaMode is SessionsSchemaMode.Current)
                    {
                        UpdateSession(conn, persistenceId, schemaMode, cmd =>
                        {
                            cmd.CommandText =
                                """
                                UPDATE sessions SET
                                    turn_count = turn_count + 1,
                                    last_activity = $now
                                WHERE persistence_id = $pid
                                """;
                        });
                    }
                    else
                    {
                        UpdateSession(conn, output.SessionId.Value, schemaMode, cmd =>
                        {
                            cmd.CommandText =
                                """
                                UPDATE sessions SET
                                    message_count = COALESCE(message_count, 0) + 1,
                                    last_activity = $now
                                WHERE session_id = $sid
                                """;
                        });
                    }

                    break;

                case UsageOutput usage when usage.InputTokens.HasValue:
                    if (schemaMode is SessionsSchemaMode.Current)
                    {
                        UpdateSession(conn, persistenceId, schemaMode, cmd =>
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
                    }
                    else
                    {
                        UpdateLastActivity(conn, output.SessionId.Value, schemaMode);
                    }

                    break;

                case SessionTitleOutput title:
                    if (schemaMode is SessionsSchemaMode.Current)
                    {
                        UpdateSession(conn, persistenceId, schemaMode, cmd =>
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
                    }
                    else
                    {
                        UpdateSession(conn, output.SessionId.Value, schemaMode, cmd =>
                        {
                            cmd.CommandText =
                                """
                                UPDATE sessions SET
                                    display_name = $title,
                                    last_activity = $now
                                WHERE session_id = $sid
                                """;
                            cmd.Parameters.AddWithValue("$title", title.Title);
                        });
                    }

                    break;

                case CompactionOutput:
                case ErrorOutput:
                    UpdateLastActivity(conn, output.SessionId.Value, schemaMode);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle output for session {SessionId}", output.SessionId.Value);
        }
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

            var schemaMode = DetectSchemaMode(conn);
            if (schemaMode is SessionsSchemaMode.Missing)
                return entries;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = schemaMode is SessionsSchemaMode.Current
                ?
                """
                SELECT persistence_id, channel, title, description, status, turn_count,
                       created_at, last_activity, log_path, last_input_tokens
                FROM sessions
                ORDER BY last_activity DESC
                LIMIT $limit
                """
                :
                """
                SELECT session_id, display_name, message_count, created, last_activity
                FROM sessions
                ORDER BY last_activity DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (schemaMode is SessionsSchemaMode.Current)
                {
                    entries.Add(new SessionCatalogEntry
                    {
                        PersistenceId = reader.GetString(0),
                        Channel = reader.GetString(1),
                        Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Status = reader.GetString(4),
                        TurnCount = reader.GetInt32(5),
                        CreatedAt = reader.GetInt64(6),
                        LastActivity = reader.GetInt64(7),
                        LogPath = reader.IsDBNull(8) ? null : reader.GetString(8),
                        LastInputTokens = reader.IsDBNull(9) ? null : reader.GetInt64(9)
                    });
                }
                else
                {
                    var sessionId = reader.GetString(0);
                    entries.Add(new SessionCatalogEntry
                    {
                        PersistenceId = $"session-{sessionId}",
                        Channel = InferChannelFromSessionId(sessionId),
                        Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Description = null,
                        Status = "active",
                        TurnCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        CreatedAt = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                        LastActivity = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        LogPath = null,
                        LastInputTokens = null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list sessions");
        }

        return entries;
    }

    private void UpdateSession(
        SqliteConnection conn,
        string sessionKey,
        SessionsSchemaMode schemaMode,
        Action<SqliteCommand> configure)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        using var cmd = conn.CreateCommand();
        configure(cmd);
        if (schemaMode is SessionsSchemaMode.Current)
            cmd.Parameters.AddWithValue("$pid", sessionKey.StartsWith("session-", StringComparison.Ordinal) ? sessionKey : $"session-{sessionKey}");
        else
            cmd.Parameters.AddWithValue("$sid", sessionKey.StartsWith("session-", StringComparison.Ordinal) ? sessionKey["session-".Length..] : sessionKey);

        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.ExecuteNonQuery();
    }

    private void UpdateLastActivity(SqliteConnection conn, string sessionId, SessionsSchemaMode schemaMode)
    {
        UpdateSession(conn, sessionId, schemaMode, cmd =>
        {
            cmd.CommandText = schemaMode is SessionsSchemaMode.Current
                ? "UPDATE sessions SET last_activity = $now WHERE persistence_id = $pid"
                : "UPDATE sessions SET last_activity = $now WHERE session_id = $sid";
        });
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

    private static string InferChannelFromSessionId(string sessionId)
    {
        if (sessionId.StartsWith("signalr/", StringComparison.Ordinal))
            return "signalr";

        if (sessionId.StartsWith("headless/", StringComparison.Ordinal))
            return "headless";

        if (sessionId.StartsWith("console/", StringComparison.Ordinal))
            return "console";

        if (sessionId.StartsWith("C", StringComparison.Ordinal)
            || sessionId.StartsWith("D", StringComparison.Ordinal))
            return "slack";

        return "unknown";
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
