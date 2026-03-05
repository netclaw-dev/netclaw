using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class SchemaMigratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public SchemaMigratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-schema-migrator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task MigrateAsync_legacy_sessions_schema_is_upgraded_and_data_preserved()
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using (var conn = new SqliteConnection(connString))
        {
            await conn.OpenAsync();

            await using var create = conn.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE sessions (
                    session_id TEXT PRIMARY KEY,
                    last_activity INTEGER NOT NULL,
                    message_count INTEGER DEFAULT 0,
                    created INTEGER DEFAULT 0,
                    display_name TEXT DEFAULT 'Unknown Session'
                );
                INSERT INTO sessions(session_id, last_activity, message_count, created, display_name)
                VALUES ('D123/1772671231.713319', 1772671231713, 4, 1772671200000, 'Legacy Session');
                """;
            await create.ExecuteNonQueryAsync();
        }

        var migrator = new SchemaMigrator(_paths, NullLogger<SchemaMigrator>.Instance);
        await migrator.MigrateAsync(_paths.SqliteDbPath, CancellationToken.None);

        await using var verifyConn = new SqliteConnection(connString);
        await verifyConn.OpenAsync();

        await using (var columnsCmd = verifyConn.CreateCommand())
        {
            columnsCmd.CommandText = "PRAGMA table_info(sessions)";
            await using var reader = await columnsCmd.ExecuteReaderAsync();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));

            Assert.Contains("persistence_id", columns);
            Assert.DoesNotContain("session_id", columns);
        }

        await using (var rowCmd = verifyConn.CreateCommand())
        {
            rowCmd.CommandText =
                "SELECT persistence_id, channel, turn_count, title FROM sessions WHERE persistence_id = 'session-D123/1772671231.713319'";
            await using var reader = await rowCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("session-D123/1772671231.713319", reader.GetString(0));
            Assert.Equal("slack", reader.GetString(1));
            Assert.Equal(4, reader.GetInt32(2));
            Assert.Equal("Legacy Session", reader.GetString(3));
        }

        await using (var schemaCmd = verifyConn.CreateCommand())
        {
            schemaCmd.CommandText = "SELECT COUNT(*) FROM schema_version";
            var appliedCount = (long)(await schemaCmd.ExecuteScalarAsync() ?? 0L);
            Assert.True(appliedCount >= 3);
        }
    }

    [Fact]
    public void SessionCatalogService_lists_legacy_sessions_when_schema_not_yet_upgraded()
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        using (var conn = new SqliteConnection(connString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE sessions (
                    session_id TEXT PRIMARY KEY,
                    last_activity INTEGER NOT NULL,
                    message_count INTEGER DEFAULT 0,
                    created INTEGER DEFAULT 0,
                    display_name TEXT DEFAULT 'Unknown Session'
                );
                INSERT INTO sessions(session_id, last_activity, message_count, created, display_name)
                VALUES ('signalr/abc123', 1772671231713, 2, 1772671200000, 'SignalR Session');
                """;
            cmd.ExecuteNonQuery();
        }

        var service = new SessionCatalogService(
            _paths,
            TimeProvider.System,
            NullLogger<SessionCatalogService>.Instance);

        var sessions = service.ListRecent();

        var entry = Assert.Single(sessions);
        Assert.Equal("session-signalr/abc123", entry.PersistenceId);
        Assert.Equal("signalr", entry.Channel);
        Assert.Equal("SignalR Session", entry.Title);
        Assert.Equal(2, entry.TurnCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_tempDir))
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    SqliteConnection.ClearAllPools();
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    SqliteConnection.ClearAllPools();
                }
            }
        }
    }
}
