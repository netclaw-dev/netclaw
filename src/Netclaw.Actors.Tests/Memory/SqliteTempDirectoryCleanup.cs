// -----------------------------------------------------------------------
// <copyright file="SqliteTempDirectoryCleanup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Deletes a test-owned directory and clears only its SQLite connection pools.
/// Fixtures can create several databases in nested directories.
/// </summary>
internal static class SqliteTempDirectoryCleanup
{
    public static Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return Task.CompletedTask;

        SqliteMemoryStoreTestPools.ClearOwnedDirectory(path);

        Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }
}
