// -----------------------------------------------------------------------
// <copyright file="SqliteTempDirectoryCleanup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Shared cleanup helper for tests that create temp SQLite files. The retry
/// loop exists because file handles can remain briefly open on Windows CI
/// after `SqliteConnection.ClearAllPools()` returns. Best-effort: leaving
/// a temp dir behind is preferable to failing a test run on cleanup.
/// </summary>
internal static class SqliteTempDirectoryCleanup
{
    public static async Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        SqliteConnection.ClearAllPools();

        for (var i = 0; i < 8; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 7)
            {
                await Task.Delay(25 * (i + 1));
            }
        }
    }
}
