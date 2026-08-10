// -----------------------------------------------------------------------
// <copyright file="SchemaMigrationHostedService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Services;

public sealed class SchemaMigrationHostedService : IHostedService
{
    private readonly DaemonPersistenceOptions _options;
    private readonly NetclawPaths _paths;
    private readonly SchemaMigrator _migrator;
    private readonly SQLiteMemoryStore _memoryStore;
    private readonly ILogger<SchemaMigrationHostedService> _logger;

    public SchemaMigrationHostedService(
        DaemonPersistenceOptions options,
        NetclawPaths paths,
        SchemaMigrator migrator,
        SQLiteMemoryStore memoryStore,
        ILogger<SchemaMigrationHostedService> logger)
    {
        _options = options;
        _paths = paths;
        _migrator = migrator;
        _memoryStore = memoryStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Provider is PersistenceProvider.Sqlite && _options.Sqlite.AutoMigrate)
        {
            var sqlitePath = ResolveSqlitePath();
            _logger.LogInformation("Running SQLite schema migrations at {Path}", sqlitePath);
            await _migrator.MigrateAsync(sqlitePath, cancellationToken);
        }

        // Memory store always uses SQLite regardless of akka persistence provider,
        // so its schema must be initialized unconditionally.
        await _memoryStore.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolveSqlitePath()
        => string.IsNullOrWhiteSpace(_options.Sqlite.Path)
            ? _paths.SqliteDbPath
            : _options.Sqlite.Path!;
}
