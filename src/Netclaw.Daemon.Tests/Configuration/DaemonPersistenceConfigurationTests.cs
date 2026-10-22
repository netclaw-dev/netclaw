// -----------------------------------------------------------------------
// <copyright file="DaemonPersistenceConfigurationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Production incident, 21 Oct 2026: a SQLite write lock held by another connection made a journal
/// write wait longer than the 10 s circuit-breaker call timeout. The breaker failed the persist,
/// and the session actor stopped. Microsoft.Data.Sqlite waits on a busy lock until its command
/// timeout (30 s by default). The journal and snapshot breakers must wait longer than that.
/// </summary>
public abstract class DaemonPersistenceTestBase : Akka.Hosting.TestKit.TestKit, IAsyncDisposable
{
    protected const string JournalPluginId = "akka.persistence.journal.sql";
    protected const string SnapshotPluginId = "akka.persistence.snapshot-store.sql";
    protected const string JournalFallbackId = "akka.persistence.journal-plugin-fallback";
    protected const string SnapshotFallbackId = "akka.persistence.snapshot-store-plugin-fallback";

    private readonly DisposableTempDir _directory = new();

    protected DaemonPersistenceTestBase(ITestOutputHelper output) : base(output: output) { }

    /// <summary>Extra connection string keys, such as <c>;Default Timeout=5</c>. Must be a constant.</summary>
    protected virtual string ConnectionStringOptions => string.Empty;

    protected string ConnectionString => $"Data Source={Path.Combine(_directory.Path, "netclaw.db")}{ConnectionStringOptions}";

    // The test host composes persistence through the same method that Program.cs calls.
    // Thus a change to that method changes the effective configuration that the tests read.
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithDaemonSqlPersistence(ConnectionString);

    // Akka builds a plugin config as the plugin section with the plugin-fallback section behind it.
    // The SQL plugin sets no call-timeout of its own, so the fallback value (10 s for the journal,
    // 20 s for the snapshot store) applies unless the daemon configuration overrides it.
    protected TimeSpan EffectiveCallTimeout(string pluginId, string fallbackId)
    {
        _ = Akka.Persistence.Persistence.Instance.Apply(Sys);
        var config = Sys.Settings.Config;
        return config.GetConfig(pluginId)
            .WithFallback(config.GetConfig(fallbackId))
            .GetTimeSpan("circuit-breaker.call-timeout");
    }

    // The wait that Microsoft.Data.Sqlite applies to a command on this exact connection string.
    // It reads the "Default Timeout" key and falls back to 30 s when the key is absent.
    protected static TimeSpan SqliteBusyWait(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        return TimeSpan.FromSeconds(connection.DefaultTimeout);
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _directory.Dispose();
        }
    }
}

public sealed class DaemonPersistenceConfigurationTests(ITestOutputHelper output) : DaemonPersistenceTestBase(output)
{
    [Fact]
    public void Circuit_breakers_wait_longer_than_the_sqlite_busy_wait()
    {
        var busyWait = SqliteBusyWait(ConnectionString);

        foreach (var (pluginId, fallbackId) in new[]
                 {
                     (JournalPluginId, JournalFallbackId),
                     (SnapshotPluginId, SnapshotFallbackId)
                 })
        {
            var callTimeout = EffectiveCallTimeout(pluginId, fallbackId);
            Assert.True(
                callTimeout > busyWait,
                $"{pluginId} call-timeout is {callTimeout}. SQLite waits {busyWait} on a busy lock. " +
                "A lock wait must not fail the persist.");
        }
    }
}

/// <summary>
/// The breaker follows the "Default Timeout" key of the connection string. A fixed breaker value
/// would pass the default-timeout test, but fail an operator who sets a different SQLite timeout.
/// </summary>
public sealed class DaemonPersistenceShortTimeoutTests(ITestOutputHelper output) : DaemonPersistenceTestBase(output)
{
    private static readonly TimeSpan ConfiguredBusyWait = TimeSpan.FromSeconds(5);

    protected override string ConnectionStringOptions => ";Default Timeout=5";

    [Fact]
    public void Circuit_breakers_derive_their_call_timeout_from_the_default_timeout_key()
    {
        Assert.Equal(ConfiguredBusyWait, SqliteBusyWait(ConnectionString));

        foreach (var (pluginId, fallbackId) in new[]
                 {
                     (JournalPluginId, JournalFallbackId),
                     (SnapshotPluginId, SnapshotFallbackId)
                 })
        {
            var callTimeout = EffectiveCallTimeout(pluginId, fallbackId);
            Assert.Equal(ConfiguredBusyWait + DaemonPersistenceConfiguration.CircuitBreakerMargin, callTimeout);
        }
    }
}

/// <summary>
/// Microsoft.Data.Sqlite treats "Default Timeout=0" as a wait without a limit. No finite breaker
/// is longer than that wait, so the daemon must reject the value. It must not fall back to a default.
/// </summary>
public sealed class DaemonPersistenceUnboundedTimeoutTests
{
    [Fact]
    public void A_default_timeout_of_zero_is_rejected_when_persistence_is_configured()
    {
        var connectionString = "Data Source=unused.db;Default Timeout=0";
        var builder = new AkkaConfigurationBuilder(new ServiceCollection(), "unbounded-timeout");

        var failure = Record.Exception(() => builder.WithDaemonSqlPersistence(connectionString));

        Assert.NotNull(failure);
        Assert.Contains("Default Timeout", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
