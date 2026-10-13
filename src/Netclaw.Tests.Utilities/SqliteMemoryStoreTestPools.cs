// -----------------------------------------------------------------------
// <copyright file="SqliteMemoryStoreTestPools.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Clears the canonical SQLiteMemoryStore pools for database files in one test-owned directory.
/// </summary>
public static class SqliteMemoryStoreTestPools
{
    public static void ClearOwnedDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var databasePath in Directory.EnumerateFiles(path, "*.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            SqliteConnection.ClearPool(connection);
        }
    }
}
