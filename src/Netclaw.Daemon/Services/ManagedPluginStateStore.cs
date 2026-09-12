// -----------------------------------------------------------------------
// <copyright file="ManagedPluginStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

internal sealed record ManagedPluginCandidate(
    string Commit,
    string Format,
    string Name,
    string? Version,
    string Directory,
    IReadOnlyList<SkillEntry> Skills,
    IReadOnlyList<string> Notices);

internal sealed record ManagedPluginReceipt(
    string SourceId,
    string Repository,
    string SourceFormat,
    string? Subdirectory,
    ManagedPluginReferenceKind ReferenceKind,
    string Reference,
    string SourceFingerprint,
    string InstalledCommit,
    string LastObservedCommit,
    string ManifestName,
    string ManifestFormat,
    string? InstalledVersion,
    DateTimeOffset InstalledAt);

internal sealed record ManagedPluginRejection(
    string SourceId,
    string SourceFingerprint,
    string Commit,
    string Reason,
    bool SecurityRejection,
    bool AlertEmitted,
    DateTimeOffset RejectedAt);

internal sealed class ManagedPluginStateStore
{
    internal const int MaximumRejectionReasonLength = 1_024;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public ManagedPluginStateStore(NetclawPaths paths, TimeProvider timeProvider)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ManagedPluginReceipt>> LoadReceiptsAsync(
        CancellationToken cancellationToken)
    {
        var receipts = new List<ManagedPluginReceipt>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_id, repository_url, source_format, plugin_subdirectory,
                   reference_kind, reference_value, source_fingerprint, installed_commit,
                   last_observed_commit, manifest_name, manifest_format, installed_version, installed_at
            FROM managed_plugin_receipts
            ORDER BY source_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new ManagedPluginReceipt(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Enum.Parse<ManagedPluginReferenceKind>(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12))));
        }

        return receipts;
    }

    public async Task<ManagedPluginReceipt?> GetReceiptAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT repository_url, source_format, plugin_subdirectory,
                   reference_kind, reference_value, source_fingerprint, installed_commit,
                   last_observed_commit, manifest_name, manifest_format, installed_version, installed_at
            FROM managed_plugin_receipts
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ManagedPluginReceipt(
            sourceId,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<ManagedPluginReferenceKind>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)));
    }

    public Task SaveReceiptAsync(
        ManagedPluginSource source,
        ManagedPluginCandidate candidate,
        CancellationToken cancellationToken)
        => SaveReceiptCoreAsync(source, candidate, clearRejection: false, cancellationToken);

    public Task SaveReceiptAfterRetryAsync(
        ManagedPluginSource source,
        ManagedPluginCandidate candidate,
        CancellationToken cancellationToken)
        => SaveReceiptCoreAsync(source, candidate, clearRejection: true, cancellationToken);

    private async Task SaveReceiptCoreAsync(
        ManagedPluginSource source,
        ManagedPluginCandidate candidate,
        bool clearRejection,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO managed_plugin_receipts (
                source_id, repository_url, source_format, plugin_subdirectory,
                reference_kind, reference_value, source_fingerprint, installed_commit,
                last_observed_commit, manifest_name, manifest_format, installed_version, installed_at)
            VALUES ($source, $repository, $format, $subdirectory,
                    $referenceKind, $reference, $fingerprint, $commit, $commit,
                    $manifestName, $manifestFormat, $version, $installedAt)
            ON CONFLICT(source_id) DO UPDATE SET
                repository_url = excluded.repository_url,
                source_format = excluded.source_format,
                plugin_subdirectory = excluded.plugin_subdirectory,
                reference_kind = excluded.reference_kind,
                reference_value = excluded.reference_value,
                source_fingerprint = excluded.source_fingerprint,
                installed_commit = excluded.installed_commit,
                last_observed_commit = excluded.last_observed_commit,
                manifest_name = excluded.manifest_name,
                manifest_format = excluded.manifest_format,
                installed_version = excluded.installed_version,
                installed_at = excluded.installed_at;
            """;
        command.Parameters.AddWithValue("$source", source.Id);
        command.Parameters.AddWithValue("$repository", source.Repository);
        command.Parameters.AddWithValue("$format", source.Format);
        command.Parameters.AddWithValue("$subdirectory", (object?)source.Subdirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$referenceKind", source.ReferenceKind.ToString());
        command.Parameters.AddWithValue("$reference", source.Reference);
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$commit", candidate.Commit);
        command.Parameters.AddWithValue("$manifestName", candidate.Name);
        command.Parameters.AddWithValue("$manifestFormat", candidate.Format);
        command.Parameters.AddWithValue("$version", (object?)candidate.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$installedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (clearRejection)
        {
            await DeleteRejectionAsync(
                connection, transaction, source.Id, fingerprint, candidate.Commit, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<bool> UpdateLastObservedCommitAsync(
        string sourceId,
        string commit,
        CancellationToken cancellationToken)
        => UpdateLastObservedCommitCoreAsync(
            sourceId, sourceFingerprint: null, commit, clearRejection: false, cancellationToken);

    public Task<bool> UpdateLastObservedCommitAfterRetryAsync(
        string sourceId,
        string sourceFingerprint,
        string commit,
        CancellationToken cancellationToken)
        => UpdateLastObservedCommitCoreAsync(
            sourceId, sourceFingerprint, commit, clearRejection: true, cancellationToken);

    private async Task<bool> UpdateLastObservedCommitCoreAsync(
        string sourceId,
        string? sourceFingerprint,
        string commit,
        bool clearRejection,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE managed_plugin_receipts
            SET last_observed_commit = $commit
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$commit", commit);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated && clearRejection)
        {
            await DeleteRejectionAsync(
                connection, transaction, sourceId, sourceFingerprint!, commit, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<ManagedPluginRejection?> GetRejectionAsync(
        string sourceId,
        string sourceFingerprint,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT reason, security_rejection, alert_emitted, rejected_at
            FROM managed_plugin_rejections
            WHERE source_id = $source AND source_fingerprint = $fingerprint
              AND commit_identity = $commit;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ManagedPluginRejection(
            sourceId,
            sourceFingerprint,
            commit,
            reader.GetString(0),
            reader.GetInt64(1) != 0,
            reader.GetInt64(2) != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    public async Task<bool> SaveRejectionAsync(
        string sourceId,
        string sourceFingerprint,
        string commit,
        string reason,
        bool securityRejection,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO managed_plugin_rejections (
                source_id, source_fingerprint, commit_identity, reason, security_rejection,
                alert_emitted, rejected_at)
            VALUES ($source, $fingerprint, $commit, $reason, $security, 0, $rejectedAt)
            ON CONFLICT(source_id, source_fingerprint, commit_identity) DO UPDATE SET
                reason = excluded.reason,
                security_rejection = MAX(
                    managed_plugin_rejections.security_rejection,
                    excluded.security_rejection),
                rejected_at = excluded.rejected_at;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        command.Parameters.AddWithValue("$reason", SanitizeReason(reason));
        command.Parameters.AddWithValue("$security", securityRejection ? 1 : 0);
        command.Parameters.AddWithValue("$rejectedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task DeleteRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        string sourceFingerprint,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM managed_plugin_rejections
            WHERE source_id = $source AND source_fingerprint = $fingerprint
              AND commit_identity = $commit;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryClaimSecurityAlertAsync(
        string sourceId,
        string sourceFingerprint,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE managed_plugin_rejections
            SET alert_emitted = 1
            WHERE source_id = $source AND source_fingerprint = $fingerprint
              AND commit_identity = $commit AND security_rejection = 1
              AND alert_emitted = 0;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RemoveSourcesExceptAsync(
        IReadOnlyCollection<string> sourceIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var names = sourceIds.ToHashSet(StringComparer.Ordinal);
        var stored = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT source_id FROM managed_plugin_receipts UNION SELECT source_id FROM managed_plugin_rejections;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                stored.Add(reader.GetString(0));
        }

        foreach (var source in stored.Where(name => !names.Contains(name)))
        {
            foreach (var table in new[] { "managed_plugin_receipts", "managed_plugin_rejections" })
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE source_id = $source;";
                delete.Parameters.AddWithValue("$source", source);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string SanitizeReason(string reason)
    {
        var sanitized = new string(reason.Select(static character => char.IsControl(character) ? ' ' : character).ToArray())
            .Trim();
        return sanitized.Length <= MaximumRejectionReasonLength
            ? sanitized
            : sanitized[..MaximumRejectionReasonLength];
    }
}
