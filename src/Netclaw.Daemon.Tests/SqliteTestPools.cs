// -----------------------------------------------------------------------
// <copyright file="SqliteTestPools.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Tests;

internal static class SqliteTestPools
{
    public static void Clear(NetclawPaths paths)
    {
        // Pool keys use the exact connection string. Both forms occur in daemon fixtures.
        var builder = new SqliteConnectionStringBuilder { DataSource = paths.SqliteDbPath };
        using var defaultMode = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(defaultMode);

        builder.Mode = SqliteOpenMode.ReadWriteCreate;
        using var explicitMode = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(explicitMode);
    }
}
