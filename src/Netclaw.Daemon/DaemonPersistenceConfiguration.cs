// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceConfiguration.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using Microsoft.Data.Sqlite;

namespace Netclaw.Daemon;

/// <summary>
/// Composes the daemon SQLite persistence for Akka. Program.cs and the tests call the same method,
/// so a test reads the configuration that the daemon uses.
/// </summary>
/// <remarks>
/// The circuit breaker does not cancel the SQL command. A lock wait shorter than the SQLite timeout only
/// delays the write. A longer lock wait fails the write with a SQLite busy error, which this class cannot
/// prevent. The breaker clock also counts the time that a write spends in the write queue.
/// </remarks>
internal static class DaemonPersistenceConfiguration
{
    /// <summary>
    /// The time that the circuit-breaker call-timeout adds to the SQLite command timeout.
    /// </summary>
    internal static readonly TimeSpan CircuitBreakerMargin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Adds SQLite journal and snapshot-store persistence. The circuit-breaker call-timeout of each
    /// store is longer than the SQLite command timeout of <paramref name="connectionString"/>.
    /// </summary>
    public static AkkaConfigurationBuilder WithDaemonSqlPersistence(
        this AkkaConfigurationBuilder builder, string connectionString)
    {
        var timeoutSeconds = new SqliteConnectionStringBuilder(connectionString).DefaultTimeout;
        if (timeoutSeconds <= 0)
            throw new ArgumentException(
                "The SQLite connection string sets Default Timeout to " + timeoutSeconds + ". SQLite would wait forever on a lock, and no circuit-breaker timeout is longer than that. Set a positive Default Timeout.",
                nameof(connectionString));

        var sqliteTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var callTimeout = sqliteTimeout + CircuitBreakerMargin;
        return builder
            .WithSqlPersistence(connectionString: connectionString, providerName: "SQLite.MS")
            .AddHocon($$"""
                akka.persistence.journal.sql.circuit-breaker.call-timeout = {{(int)callTimeout.TotalSeconds}}s
                akka.persistence.snapshot-store.sql.circuit-breaker.call-timeout = {{(int)callTimeout.TotalSeconds}}s
                """, HoconAddMode.Prepend);
    }
}
