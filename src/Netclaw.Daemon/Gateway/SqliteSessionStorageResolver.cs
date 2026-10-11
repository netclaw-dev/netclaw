// -----------------------------------------------------------------------
// <copyright file="SqliteSessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Resolves an immutable session storage binding with one immediate SQLite transaction.
/// </summary>
public sealed class SqliteSessionStorageResolver : ISessionStorageResolver
{
    private abstract record SessionStorageDatabaseState
    {
        public sealed record Version2(string StoredEnvelopeRoot) : SessionStorageDatabaseState;

        public sealed record Legacy : SessionStorageDatabaseState;

        public sealed record New : SessionStorageDatabaseState;
    }

    private readonly string _connectionString;
    private readonly string _sessionsDirectory;
    private readonly string _sessionLogsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteSessionStorageResolver> _logger;
    // GetOrAdd can run the resolve factory twice for one session; this keeps the re-root warning to once.
    private readonly ConcurrentDictionary<string, byte> _reRootedSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionStoragePaths> _resolved =
        new(StringComparer.Ordinal);

    /// <summary>Creates a resolver that stores bindings in the Netclaw database.</summary>
    /// <param name="paths">The Netclaw filesystem paths.</param>
    /// <param name="timeProvider">The clock used for binding timestamps.</param>
    public SqliteSessionStorageResolver(
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<SqliteSessionStorageResolver>? logger = null)
        : this(paths, timeProvider, paths.SessionsDirectory, logger)
    {
    }

    internal SqliteSessionStorageResolver(
        NetclawPaths paths,
        TimeProvider timeProvider,
        string sessionsDirectory,
        ILogger<SqliteSessionStorageResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Cache misses own their native connection; shared pool state and reclamation cannot affect the transaction.
            Pooling = false
        }.ToString();
        _sessionsDirectory = Path.GetFullPath(sessionsDirectory);
        _sessionLogsDirectory = paths.SessionLogsDirectory;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<SqliteSessionStorageResolver>.Instance;
    }

    /// <inheritdoc />
    public SessionStoragePaths Resolve(SessionId sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId.Value);

        return _resolved.GetOrAdd(sessionId.Value, _ => ResolveUncached(sessionId));
    }

    private SessionStoragePaths ResolveUncached(SessionId sessionId)
    {
        var sanitizedSessionId = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        var legacySessionDirectory = SessionDirectoryHelper.GetSessionDirectory(
            sessionId,
            _sessionsDirectory);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var hasLegacyFiles = Directory.Exists(legacySessionDirectory)
                             || Directory.Exists(Path.Combine(_sessionLogsDirectory, sanitizedSessionId));
        var state = ReadDatabaseState(connection, transaction, sessionId, hasLegacyFiles);

        SessionStoragePaths storage;
        switch (state)
        {
            case SessionStorageDatabaseState.Version2(var storedEnvelopeRoot):
                var resolvedRoot = ResolveStoredEnvelopeRoot(storedEnvelopeRoot, _sessionsDirectory);
                if (!string.Equals(resolvedRoot, storedEnvelopeRoot, StringComparison.Ordinal)
                    && _reRootedSessions.TryAdd(sessionId.Value, 0))
                {
                    _logger.LogWarning(
                        "Session {SessionId} was stored at {StoredEnvelopeRoot}; using {EnvelopeRoot} under the current data directory{Missing}",
                        sessionId.Value,
                        storedEnvelopeRoot,
                        resolvedRoot,
                        Directory.Exists(resolvedRoot) ? string.Empty : " (that folder does not exist yet, so the session starts with an empty workspace)");
                }

                storage = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(resolvedRoot));
                break;
            case SessionStorageDatabaseState.Legacy:
                storage = SessionStoragePaths.CreateLegacy(
                    legacySessionDirectory,
                    _sessionLogsDirectory,
                    sanitizedSessionId);
                break;
            case SessionStorageDatabaseState.New:
                var envelopeRoot = new SessionStorageEnvelopeRoot(
                    Path.Combine(_sessionsDirectory, CreateEnvelopeDirectoryName(sessionId, sanitizedSessionId)));
                var newBinding = new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot);
                InsertBinding(connection, transaction, sessionId, newBinding);
                storage = SessionStoragePaths.CreateVersion2(envelopeRoot);
                break;
            default:
                throw new InvalidOperationException("The session storage database returned an invalid state.");
        }

        transaction.Commit();
        return storage;
    }

    /// <summary>
    /// Maps a persisted envelope root to this home. The database and the sessions directory always live
    /// in the same home, so an envelope named <c>sessions/&lt;name&gt;</c> in the stored absolute path belongs under
    /// <paramref name="sessionsDirectory"/> wherever the home was restored. Both separator styles are read, so a
    /// Windows-written value moves to Linux. A stored value of any other shape is returned unchanged.
    /// </summary>
    internal static string ResolveStoredEnvelopeRoot(string stored, string sessionsDirectory)
    {
        var segments = stored.Split('/', '\\');
        if (segments.Length < 2
            || segments[^2] != "sessions"
            || segments[^1].Length == 0
            || segments[^1] is "." or "..")
        {
            return stored;
        }

        return Path.Combine(sessionsDirectory, segments[^1]);
    }

    private static SessionStorageDatabaseState ReadDatabaseState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        bool hasLegacyFiles)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                binding.layout_version,
                binding.envelope_root,
                EXISTS(
                    SELECT 1
                    FROM sessions
                    WHERE persistence_id = $persistenceId),
                EXISTS(
                    SELECT 1
                    FROM journal
                    WHERE persistence_id = $persistenceId),
                EXISTS(
                    SELECT 1
                    FROM snapshot
                    WHERE persistence_id = $persistenceId),
                EXISTS(
                    SELECT 1
                    FROM journal_metadata
                    WHERE persistence_id = $persistenceId)
            FROM (SELECT 1) AS singleton
            LEFT JOIN session_storage_bindings AS binding
                ON binding.session_id = $sessionId
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The session storage query returned no state row.");

        if (!reader.IsDBNull(0))
        {
            var version = new SessionStorageLayoutVersion(reader.GetInt32(0));
            if (version != SessionStorageLayoutVersion.Version2)
            {
                throw new NotSupportedException(
                    $"Session '{sessionId.Value}' uses unsupported storage layout version {version.Value}.");
            }

            return new SessionStorageDatabaseState.Version2(reader.GetString(1));
        }

        var hasPersistedLegacySession = reader.GetInt64(2) != 0
                                        || reader.GetInt64(3) != 0
                                        || reader.GetInt64(4) != 0
                                        || reader.GetInt64(5) != 0;
        return hasLegacyFiles || hasPersistedLegacySession
            ? new SessionStorageDatabaseState.Legacy()
            : new SessionStorageDatabaseState.New();
    }

    private void InsertBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        SessionStorageBinding binding)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO session_storage_bindings(
                session_id,
                layout_version,
                envelope_root,
                created_at)
            VALUES ($sessionId, $layoutVersion, $envelopeRoot, $createdAt)
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        command.Parameters.AddWithValue("$layoutVersion", binding.LayoutVersion.Value);
        command.Parameters.AddWithValue("$envelopeRoot", binding.EnvelopeRoot.Value);
        command.Parameters.AddWithValue(
            "$createdAt",
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static string CreateEnvelopeDirectoryName(SessionId sessionId, string sanitizedSessionId)
    {
        const int displayPrefixLength = 80;
        var displayPrefix = sanitizedSessionId.Length <= displayPrefixLength
            ? sanitizedSessionId
            : sanitizedSessionId[..displayPrefixLength];
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Value));
        var suffix = Convert.ToHexStringLower(digest.AsSpan(0, 8));
        return $"{displayPrefix}-{suffix}";
    }
}
