using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Singleton service maintaining the <c>sessions</c> table in the SQLite database.
/// Provides write methods called from <see cref="SessionRegistry"/> and output
/// subscribers, plus read methods for the <c>GET /api/sessions</c> endpoint.
/// </summary>
public sealed class SessionCatalogService
{
    private readonly string _connectionString;
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionCatalogService> _logger;

    // Per-session log writers (created on session start, flushed on each write)
    private readonly Dictionary<string, StreamWriter> _logWriters = new();

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

    /// <summary>
    /// Record a new session in the catalog. Called when a session is created.
    /// </summary>
    public void RecordSessionCreated(SessionId sessionId, string channel)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var persistenceId = $"session-{sessionId.Value}";

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count)
                VALUES ($pid, $channel, $now, $now, 'active', 0)
                ON CONFLICT(persistence_id) DO UPDATE SET
                    status = 'active',
                    last_activity = $now
                """;
            cmd.Parameters.AddWithValue("$pid", persistenceId);
            cmd.Parameters.AddWithValue("$channel", channel);
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.ExecuteNonQuery();

            // Set up per-session log file
            var logPath = GetSessionLogPath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
            _logWriters[persistenceId] = writer;
            writer.WriteLine($"[{_timeProvider.GetUtcNow():o}] Session created: {sessionId.Value} channel={channel}");

            // Update log_path in DB
            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE sessions SET log_path = $path WHERE persistence_id = $pid";
            updateCmd.Parameters.AddWithValue("$path", logPath);
            updateCmd.Parameters.AddWithValue("$pid", persistenceId);
            updateCmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record session creation for {SessionId}", sessionId.Value);
        }
    }

    /// <summary>
    /// Process a session output event — updates the catalog and writes to the session log.
    /// </summary>
    public void HandleOutput(SessionOutput output)
    {
        var persistenceId = $"session-{output.SessionId.Value}";

        try
        {
            switch (output)
            {
                case TurnCompleted tc:
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
                    LogToSession(persistenceId, $"Turn {tc.TurnNumber} completed");
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
                    LogToSession(persistenceId, $"Title set: {title.Title}");
                    break;

                case CompactionOutput compaction:
                    LogToSession(persistenceId,
                        $"Compaction: {compaction.MessagesBefore} → {compaction.MessagesAfter} messages " +
                        $"(keep={compaction.KeepCountUsed}, context={compaction.PreCompactionInputTokens}/{compaction.ContextWindowTokens} tokens)");
                    UpdateLastActivity(persistenceId);
                    break;

                case ErrorOutput error:
                    LogToSession(persistenceId, $"Error: {error.Message}");
                    UpdateLastActivity(persistenceId);
                    break;

                case TextOutput text:
                    LogToSession(persistenceId, $"Assistant: {Truncate(text.Text, 200)}");
                    break;

                case ToolCallOutput toolCall:
                    LogToSession(persistenceId, $"Tool call: {toolCall.ToolName} (call={toolCall.CallId})");
                    break;

                case ToolResultOutput toolResult:
                    LogToSession(persistenceId, $"Tool result: {toolResult.ToolName} (call={toolResult.CallId}) → {Truncate(toolResult.Result, 200)}");
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
                       created_at, last_activity, log_path
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
                    LogPath = reader.IsDBNull(8) ? null : reader.GetString(8)
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
    /// Flush and close all open session log writers.
    /// </summary>
    public void Dispose()
    {
        foreach (var writer in _logWriters.Values)
        {
            try { writer.Dispose(); } catch { /* best effort */ }
        }
        _logWriters.Clear();
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

    private void LogToSession(string persistenceId, string message)
    {
        if (_logWriters.TryGetValue(persistenceId, out var writer))
        {
            writer.WriteLine($"[{_timeProvider.GetUtcNow():o}] {message}");
        }
    }

    private string GetSessionLogPath(SessionId sessionId)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId.Value);
        return Path.Combine(_paths.SessionLogsDirectory, $"{sanitized}.log");
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");
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
}
