using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

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

        var applied = await ReadAppliedVersionsAsync(conn, cancellationToken);
        var migrations = DiscoverMigrations();

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
        if (!Directory.Exists(migrationRoot))
            return [];

        return Directory.GetFiles(migrationRoot, "*.sql", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileName(path),
                Version = ParseVersion(Path.GetFileName(path))
            })
            .Where(x => x.Version is not null)
            .OrderBy(x => x.Version)
            .Select(x => new SqlMigration(x.Version!.Value, x.Name, File.ReadAllText(x.Path)))
            .ToList();
    }

    private static int? ParseVersion(string fileName)
    {
        var first = fileName.Split('_', 2)[0];
        return int.TryParse(first, out var v) ? v : null;
    }

    private sealed record SqlMigration(int Version, string Name, string Sql);
}
