// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon.Configuration;

public enum PersistenceProvider
{
    Sqlite,
    InMemory
}

public sealed class DaemonPersistenceOptions
{
    public PersistenceProvider Provider { get; init; } = PersistenceProvider.Sqlite;

    public SqlitePersistenceOptions Sqlite { get; init; } = new();
}

public sealed class SqlitePersistenceOptions
{
    public string? Path { get; init; }

    public bool AutoMigrate { get; init; } = true;
}
