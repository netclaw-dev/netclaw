using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Services;

public sealed class SchemaMigrationHostedService : IHostedService
{
    private readonly DaemonPersistenceOptions _options;
    private readonly NetclawPaths _paths;
    private readonly SchemaMigrator _migrator;
    private readonly ILogger<SchemaMigrationHostedService> _logger;

    public SchemaMigrationHostedService(
        DaemonPersistenceOptions options,
        NetclawPaths paths,
        SchemaMigrator migrator,
        ILogger<SchemaMigrationHostedService> logger)
    {
        _options = options;
        _paths = paths;
        _migrator = migrator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Provider is not PersistenceProvider.Sqlite)
            return;

        if (!_options.Sqlite.AutoMigrate)
            return;

        var sqlitePath = ResolveSqlitePath();
        _logger.LogInformation("Running SQLite schema migrations at {Path}", sqlitePath);
        await _migrator.MigrateAsync(sqlitePath, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolveSqlitePath()
        => string.IsNullOrWhiteSpace(_options.Sqlite.Path)
            ? _paths.SqliteDbPath
            : _options.Sqlite.Path!;
}
