// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

internal sealed record GitSkillPluginReceipt(
    string SourceName,
    string Repository,
    string Format,
    string? Subdirectory,
    GitSkillPluginReferenceKind ReferenceKind,
    string Reference,
    string SourceFingerprint,
    string InstalledCommit,
    string LastObservedCommit,
    string? InstalledVersion,
    DateTimeOffset InstalledAt);

internal sealed record GitSkillPluginRejection(
    string SourceName,
    string SourceFingerprint,
    string Commit,
    string Reason,
    bool SecurityRejection,
    bool AlertEmitted,
    DateTimeOffset RejectedAt);

internal sealed class GitSkillPluginStateStore
{
    internal const int MaximumRejectionReasonLength = 1_024;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public GitSkillPluginStateStore(NetclawPaths paths, TimeProvider timeProvider)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<GitSkillPluginReceipt>> LoadReceiptsAsync(
        CancellationToken cancellationToken)
    {
        var receipts = new List<GitSkillPluginReceipt>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_name, repository_url, plugin_format, plugin_subdirectory,
                   reference_kind, reference_value, source_fingerprint, installed_commit,
                   last_observed_commit, installed_version, installed_at
            FROM git_skill_plugin_receipts
            ORDER BY source_name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new GitSkillPluginReceipt(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Enum.Parse<GitSkillPluginReferenceKind>(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10))));
        }

        return receipts;
    }

    public async Task<GitSkillPluginReceipt?> GetReceiptAsync(
        string sourceName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT repository_url, plugin_format, plugin_subdirectory,
                   reference_kind, reference_value, source_fingerprint, installed_commit,
                   last_observed_commit, installed_version, installed_at
            FROM git_skill_plugin_receipts
            WHERE source_name = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new GitSkillPluginReceipt(
            sourceName,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<GitSkillPluginReferenceKind>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)));
    }

    public async Task SaveReceiptAsync(
        GitSkillPluginSource source,
        string commit,
        string? version,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO git_skill_plugin_receipts (
                source_name, repository_url, plugin_format, plugin_subdirectory,
                reference_kind, reference_value, source_fingerprint, installed_commit,
                last_observed_commit, installed_version, installed_at)
            VALUES ($source, $repository, $format, $subdirectory,
                    $referenceKind, $reference, $fingerprint, $commit, $commit, $version, $installedAt)
            ON CONFLICT(source_name) DO UPDATE SET
                repository_url = excluded.repository_url,
                plugin_format = excluded.plugin_format,
                plugin_subdirectory = excluded.plugin_subdirectory,
                reference_kind = excluded.reference_kind,
                reference_value = excluded.reference_value,
                source_fingerprint = excluded.source_fingerprint,
                installed_commit = excluded.installed_commit,
                last_observed_commit = excluded.last_observed_commit,
                installed_version = excluded.installed_version,
                installed_at = excluded.installed_at;
            """;
        command.Parameters.AddWithValue("$source", source.Name);
        command.Parameters.AddWithValue("$repository", source.Repository);
        command.Parameters.AddWithValue("$format", source.Format);
        command.Parameters.AddWithValue("$subdirectory", (object?)source.Subdirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$referenceKind", source.ReferenceKind.ToString());
        command.Parameters.AddWithValue("$reference", source.Reference);
        command.Parameters.AddWithValue("$fingerprint", GitSkillPluginSourceValidator.Fingerprint(source));
        command.Parameters.AddWithValue("$commit", commit);
        command.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
        command.Parameters.AddWithValue("$installedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpdateLastObservedCommitAsync(
        string sourceName,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE git_skill_plugin_receipts
            SET last_observed_commit = $commit
            WHERE source_name = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$commit", commit);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<GitSkillPluginRejection?> GetRejectionAsync(
        string sourceName,
        string sourceFingerprint,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT reason, security_rejection, alert_emitted, rejected_at
            FROM git_skill_plugin_rejections
            WHERE source_name = $source AND source_fingerprint = $fingerprint
              AND commit_identity = $commit;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new GitSkillPluginRejection(
            sourceName,
            sourceFingerprint,
            commit,
            reader.GetString(0),
            reader.GetInt64(1) != 0,
            reader.GetInt64(2) != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    public async Task<bool> SaveRejectionAsync(
        string sourceName,
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
            INSERT INTO git_skill_plugin_rejections (
                source_name, source_fingerprint, commit_identity, reason, security_rejection,
                alert_emitted, rejected_at)
            VALUES ($source, $fingerprint, $commit, $reason, $security, 0, $rejectedAt)
            ON CONFLICT(source_name, source_fingerprint, commit_identity) DO UPDATE SET
                reason = excluded.reason,
                security_rejection = MAX(
                    git_skill_plugin_rejections.security_rejection,
                    excluded.security_rejection),
                rejected_at = excluded.rejected_at;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        command.Parameters.AddWithValue("$reason", SanitizeReason(reason));
        command.Parameters.AddWithValue("$security", securityRejection ? 1 : 0);
        command.Parameters.AddWithValue("$rejectedAt", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> TryClaimSecurityAlertAsync(
        string sourceName,
        string sourceFingerprint,
        string commit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE git_skill_plugin_rejections
            SET alert_emitted = 1
            WHERE source_name = $source AND source_fingerprint = $fingerprint
              AND commit_identity = $commit AND security_rejection = 1
              AND alert_emitted = 0;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$commit", commit);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RemoveSourcesExceptAsync(
        IReadOnlyCollection<string> sourceNames,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var names = sourceNames.ToHashSet(StringComparer.Ordinal);
        var stored = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT source_name FROM git_skill_plugin_receipts UNION SELECT source_name FROM git_skill_plugin_rejections;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                stored.Add(reader.GetString(0));
        }

        foreach (var source in stored.Where(name => !names.Contains(name)))
        {
            foreach (var table in new[] { "git_skill_plugin_receipts", "git_skill_plugin_rejections" })
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE source_name = $source;";
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
