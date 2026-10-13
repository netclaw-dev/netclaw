// -----------------------------------------------------------------------
// <copyright file="SqliteTempDirectoryCleanupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class SqliteTempDirectoryCleanupTests
{
    [Fact]
    public async Task Cleanup_clears_its_database_pool_and_preserves_another_idle_pool()
    {
        // Arrange
        await using var ownerA = TestSessionTempDirectory.Create("netclaw-pool-owner-a-");
        await using var ownerB = TestSessionTempDirectory.Create("netclaw-pool-owner-b-");
        try
        {
            var connectionStringA = ConnectionString(Path.Combine(ownerA.Path, "memory.db"));
            var connectionStringB = ConnectionString(Path.Combine(ownerB.Path, "memory.db"));
            var handleA = await OpenAndReturnHandleAsync(connectionStringA);
            var handleB = await OpenAndReturnHandleAsync(connectionStringB);

            // Act
            await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(ownerB.Path);
            var removedB = !Directory.Exists(ownerB.Path);
            Directory.CreateDirectory(ownerB.Path);
            var nextHandleB = await OpenAndReturnHandleAsync(connectionStringB);
            var nextHandleA = await OpenAndReturnHandleAsync(connectionStringA);

            // Assert
            Assert.True(removedB);
            Assert.NotSame(handleB, nextHandleB);
            Assert.Same(handleA, nextHandleA);
        }
        finally
        {
            try
            {
                await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(ownerB.Path);
            }
            finally
            {
                await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(ownerA.Path);
            }
        }
    }

    [Fact]
    public async Task Cleanup_of_another_database_keeps_an_active_command_valid()
    {
        // Arrange
        await using var ownerA = TestSessionTempDirectory.Create("netclaw-active-owner-a-");
        await using var ownerB = TestSessionTempDirectory.Create("netclaw-active-owner-b-");
        try
        {
            var connectionStringA = ConnectionString(Path.Combine(ownerA.Path, "memory.db"));
            var connectionStringB = ConnectionString(Path.Combine(ownerB.Path, "memory.db"));
            await OpenAndReturnHandleAsync(connectionStringB);
            await using var connectionA = new SqliteConnection(connectionStringA);
            await connectionA.OpenAsync(TestContext.Current.CancellationToken);
            var activeHandle = connectionA.Handle;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            connectionA.CreateFunction("wait_for_release", () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The active SQLite command did not receive its release signal.");
                return 17L;
            });
            await using var command = connectionA.CreateCommand();
            command.CommandText = "SELECT wait_for_release()";
            var resultTask = Task.Run(() => command.ExecuteScalar(), TestContext.Current.CancellationToken);

            // Act
            object? result = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(ownerB.Path);
            }
            finally
            {
                release.Set();
                result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            // Assert
            Assert.False(Directory.Exists(ownerB.Path));
            Assert.Equal(17L, result);
            Assert.Same(activeHandle, connectionA.Handle);
        }
        finally
        {
            try
            {
                await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(ownerB.Path);
            }
            finally
            {
                await SqliteTempDirectoryCleanup.TryDeleteDirectoryAsync(ownerA.Path);
            }
        }
    }

    private static string ConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    private static async Task<object> OpenAndReturnHandleAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection.Handle
            ?? throw new InvalidOperationException("The open SQLite connection has no native handle.");
    }
}
