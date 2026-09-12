// -----------------------------------------------------------------------
// <copyright file="SchemaMigrator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using System.Reflection;

namespace Netclaw.Daemon.Services;

public sealed class SchemaMigrator
{
    private readonly NetclawPaths _paths;
    private readonly ILogger<SchemaMigrator> _logger;

    public SchemaMigrator(NetclawPaths paths, ILogger<SchemaMigrator> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task MigrateAsync(string sqlitePath, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectoriesExist();
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath) ?? _paths.BasePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await EnsureSchemaVersionTableAsync(conn, cancellationToken);
        await EnsureLegacySessionsTableCompatibilityAsync(conn, cancellationToken);

        var applied = await ReadAppliedVersionsAsync(conn, cancellationToken);
        var migrations = DiscoverMigrations();

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException(
                "No SQLite migrations were discovered. Ensure migration assets are included in the published binary.");
        }

        foreach (var migration in migrations)
        {
            if (applied.Contains(migration.Version))
                continue;

            _logger.LogInformation("Applying SQLite migration {Version}: {Name}", migration.Version, migration.Name);

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_version(version, name, applied_at) VALUES ($v, $n, unixepoch())";
                cmd.Parameters.AddWithValue("$v", migration.Version);
                cmd.Parameters.AddWithValue("$n", migration.Name);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        _logger.LogInformation("SQLite schema migration complete ({Count} migration files discovered)", migrations.Count);
    }

    private async Task EnsureLegacySessionsTableCompatibilityAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        var columns = await ReadTableColumnsAsync(conn, "sessions", cancellationToken);
        if (columns.Count == 0)
            return;

        var hasCurrentSchema = columns.Contains("persistence_id");
        var hasLegacySchema = columns.Contains("session_id");

        if (hasCurrentSchema || !hasLegacySchema)
            return;

        _logger.LogWarning(
            "Detected legacy sessions schema (session_id/message_count/display_name). Migrating to current schema.");

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "ALTER TABLE sessions RENAME TO sessions_legacy";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                CREATE TABLE sessions (
                    persistence_id  TEXT NOT NULL PRIMARY KEY,
                    channel         TEXT NOT NULL,
                    created_at      INTEGER NOT NULL,
                    last_activity   INTEGER NOT NULL,
                    status          TEXT NOT NULL DEFAULT 'active',
                    turn_count      INTEGER NOT NULL DEFAULT 0,
                    title           TEXT,
                    description     TEXT,
                    last_input_tokens INTEGER,
                    log_path        TEXT,
                    metadata        TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO sessions (
                    persistence_id,
                    channel,
                    created_at,
                    last_activity,
                    status,
                    turn_count,
                    title,
                    description,
                    last_input_tokens,
                    log_path,
                    metadata)
                SELECT
                    'session-' || session_id,
                    CASE
                        WHEN session_id LIKE 'signalr/%' THEN 'signalr'
                        WHEN session_id LIKE 'headless/%' THEN 'headless'
                        WHEN session_id LIKE 'console/%' THEN 'console'
                        WHEN session_id LIKE 'C%/%' OR session_id LIKE 'D%/%' THEN 'slack'
                        ELSE 'unknown'
                    END,
                    COALESCE(created, last_activity, 0),
                    COALESCE(last_activity, created, 0),
                    'active',
                    COALESCE(message_count, 0),
                    NULLIF(display_name, ''),
                    NULL,
                    NULL,
                    NULL,
                    NULL
                FROM sessions_legacy;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DROP TABLE sessions_legacy";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions (status);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_sessions_last_activity ON sessions (last_activity);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task EnsureSchemaVersionTableAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var versions = new HashSet<int>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static List<SqlMigration> DiscoverMigrations()
    {
        var migrationRoot = Path.Combine(AppContext.BaseDirectory, "migrations", "sqlite");
        if (Directory.Exists(migrationRoot))
        {
            return [.. Directory.GetFiles(migrationRoot, "*.sql", SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Version = ParseVersion(Path.GetFileName(path))
                })
                .Where(x => x.Version is not null)
                .OrderBy(x => x.Version)
                .Select(x => new SqlMigration(x.Version!.Value, x.Name, File.ReadAllText(x.Path)))];
        }

        var marker = ".migrations.sqlite.";
        var assembly = typeof(SchemaMigrator).Assembly;

        return [.. assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                           && name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                ResourceName = path,
                Name = ExtractMigrationName(path, marker)
            })
            .Where(x => x.Name is not null)
            .Select(x => new
            {
                x.ResourceName,
                Name = x.Name!,
                Version = ParseVersion(x.Name!)
            })
            .Where(x => x.Version is not null)
            .OrderBy(x => x.Version)
            .Select(x => new SqlMigration(
                x.Version!.Value,
                x.Name,
                ReadEmbeddedResourceText(assembly, x.ResourceName)))];
    }

    private static async Task<HashSet<string>> ReadTableColumnsAsync(
        SqliteConnection conn,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
                columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string? ExtractMigrationName(string resourceName, string marker)
    {
        var idx = resourceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        return resourceName[(idx + marker.Length)..];
    }

    private static string ReadEmbeddedResourceText(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int? ParseVersion(string fileName)
    {
        var first = fileName.Split('_', 2)[0];
        return int.TryParse(first, out var v) ? v : null;
    }

    private sealed record SqlMigration(int Version, string Name, string Sql);
}
