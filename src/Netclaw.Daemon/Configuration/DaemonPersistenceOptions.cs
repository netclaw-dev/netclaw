namespace Netclaw.Daemon.Configuration;

public sealed class DaemonPersistenceOptions
{
    public string Provider { get; init; } = "Sqlite";

    public SqlitePersistenceOptions Sqlite { get; init; } = new();
}

public sealed class SqlitePersistenceOptions
{
    public string? Path { get; init; }

    public bool AutoMigrate { get; init; } = true;
}
