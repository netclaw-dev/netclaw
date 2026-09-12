// -----------------------------------------------------------------------
// <copyright file="SchemaMigrationHostedService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>Applies required SQLite schema migrations before dependent hosted services start.</summary>
public sealed class SchemaMigrationHostedService : IHostedService
{
    private readonly NetclawPaths _paths;
    private readonly SchemaMigrator _migrator;
    private readonly SQLiteMemoryStore _memoryStore;
    private readonly ILogger<SchemaMigrationHostedService> _logger;

    /// <summary>Creates the startup migration service.</summary>
    /// <param name="paths">The daemon filesystem paths.</param>
    /// <param name="migrator">The SQLite schema migrator.</param>
    /// <param name="memoryStore">The memory store in the selected daemon database.</param>
    /// <param name="logger">The startup logger.</param>
    public SchemaMigrationHostedService(
        NetclawPaths paths,
        SchemaMigrator migrator,
        SQLiteMemoryStore memoryStore,
        ILogger<SchemaMigrationHostedService> logger)
    {
        _paths = paths;
        _migrator = migrator;
        _memoryStore = memoryStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running SQLite schema migrations at {Path}", _paths.SqliteDbPath);
        await _migrator.MigrateAsync(_paths.SqliteDbPath, cancellationToken);

        // The memory schema lives in the same database as every other SQLite-backed feature.
        await _memoryStore.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
