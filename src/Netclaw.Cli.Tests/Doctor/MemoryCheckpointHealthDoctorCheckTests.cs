// -----------------------------------------------------------------------
// <copyright file="MemoryCheckpointHealthDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Memory;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class MemoryCheckpointHealthDoctorCheckTests
{
    [Fact]
    public async Task Passes_WhenSqliteMemoryQueueIsSmall()
    {
        var paths = CreateTempPaths();
        WriteMemoryProvider(paths, "sqlite");

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.EnqueueCheckpointAsync(new SQLiteMemoryCheckpoint(
            CheckpointId: "cp-1",
            SessionId: "chan/thread",
            TurnId: "turn-1",
            TriggerType: "turn-complete",
            Priority: 10,
            Status: "pending",
            PayloadJson: "{}",
            RetryCount: 0,
            CreatedAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
            UpdatedAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()), TestContext.Current.CancellationToken);

        await ForceWalCheckpointAsync(paths.MemorySqliteDbPath, TestContext.Current.CancellationToken);

        var check = new MemoryCheckpointHealthDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("pending checkpoints", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Warns_WhenSqliteMemoryQueueBacklogIsHigh()
    {
        var paths = CreateTempPaths();
        WriteMemoryProvider(paths, "sqlite");

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        for (var i = 0; i < 30; i++)
        {
            await store.EnqueueCheckpointAsync(new SQLiteMemoryCheckpoint(
                CheckpointId: $"cp-{i}",
                SessionId: "chan/thread",
                TurnId: $"turn-{i}",
                TriggerType: "turn-complete",
                Priority: 10,
                Status: "pending",
                PayloadJson: "{}",
                RetryCount: 0,
                CreatedAtMs: now,
                UpdatedAtMs: now), TestContext.Current.CancellationToken);
        }

        await ForceWalCheckpointAsync(paths.MemorySqliteDbPath, TestContext.Current.CancellationToken);

        var check = new MemoryCheckpointHealthDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("pending checkpoints", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static void WriteMemoryProvider(NetclawPaths paths, string provider)
    {
        var config = new Dictionary<string, object>
        {
            ["Memory"] = new Dictionary<string, object>
            {
                ["Provider"] = provider
            }
        };

        File.WriteAllText(paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ForceWalCheckpointAsync(string dbPath, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
