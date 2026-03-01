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

            using var cmd = conn.CreateCommand();
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
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.Parameters.AddWithValue("$path", logPath);
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
            switch (output)
            {
                case TurnCompleted:
                    UpdateSession(persistenceId, cmd =>
                    {
                        cmd.CommandText =
                            """
                            UPDATE sessions SET
                                turn_count = turn_count + 1,
                                last_activity = $now
                            WHERE persistence_id = $pid
                            """;
                    });
                    break;

                case UsageOutput usage when usage.InputTokens.HasValue:
                    UpdateSession(persistenceId, cmd =>
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
                    UpdateSession(persistenceId, cmd =>
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
                    UpdateLastActivity(persistenceId);
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
                    Status = reader.GetString(4),
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

    private void UpdateSession(string persistenceId, Action<SqliteCommand> configure)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        configure(cmd);
        cmd.Parameters.AddWithValue("$pid", persistenceId);
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.ExecuteNonQuery();
    }

    private void UpdateLastActivity(string persistenceId)
    {
        UpdateSession(persistenceId, cmd =>
        {
            cmd.CommandText = "UPDATE sessions SET last_activity = $now WHERE persistence_id = $pid";
        });
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
