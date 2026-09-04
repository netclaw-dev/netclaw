// -----------------------------------------------------------------------
// <copyright file="SqliteSessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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
        public sealed record Version2(SessionStorageBinding Binding) : SessionStorageDatabaseState;

        public sealed record Legacy : SessionStorageDatabaseState;

        public sealed record New : SessionStorageDatabaseState;
    }

    private readonly string _connectionString;
    private readonly string _sessionsDirectory;
    private readonly string _sessionLogsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SessionStoragePaths> _resolved =
        new(StringComparer.Ordinal);

    /// <summary>Creates a resolver that stores bindings in the Netclaw database.</summary>
    /// <param name="paths">The Netclaw filesystem paths.</param>
    /// <param name="timeProvider">The clock used for binding timestamps.</param>
    public SqliteSessionStorageResolver(
        NetclawPaths paths,
        TimeProvider timeProvider)
        : this(paths, timeProvider, paths.SessionsDirectory)
    {
    }

    internal SqliteSessionStorageResolver(
        NetclawPaths paths,
        TimeProvider timeProvider,
        string sessionsDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _sessionsDirectory = Path.GetFullPath(sessionsDirectory);
        _sessionLogsDirectory = paths.SessionLogsDirectory;
        _timeProvider = timeProvider;
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
            case SessionStorageDatabaseState.Version2(var binding):
                storage = SessionStoragePaths.CreateVersion2(binding.EnvelopeRoot);
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

            return new SessionStorageDatabaseState.Version2(
                new SessionStorageBinding(
                    version,
                    new SessionStorageEnvelopeRoot(reader.GetString(1))));
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
