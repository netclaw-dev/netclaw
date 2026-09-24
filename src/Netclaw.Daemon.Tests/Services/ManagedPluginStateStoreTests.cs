// -----------------------------------------------------------------------
// <copyright file="ManagedPluginStateStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.Sqlite;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ManagedPluginStateStoreTests : IDisposable
{
    private const string Commit = "13e26d39ed01d97ea592235d041304d289f4ba07";
    private const string LaterCommit = "23e26d39ed01d97ea592235d041304d289f4ba08";
    private readonly DisposableTempDir _temp = new();
    private readonly NetclawPaths _paths;

    public ManagedPluginStateStoreTests() => _paths = new NetclawPaths(_temp.Path);

    public void Dispose()
    {
        SqliteTestPools.Clear(_paths);
        _temp.Dispose();
    }

    [Fact]
    public async Task Migration_and_store_preserve_receipts_and_scope_rejections_by_fingerprint()
    {
        var paths = _paths;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var migrator = new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance);
        await migrator.MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(paths, time);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);

        Assert.True(await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "bad content", true,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.GetRejectionAsync(
            source.Id, new string('a', 64), Commit,
            TestContext.Current.CancellationToken));

        await store.SaveReceiptAsync(source, Candidate(Commit, "1.0.0"), TestContext.Current.CancellationToken);

        var receipt = Assert.Single(await store.LoadReceiptsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fingerprint, receipt.SourceFingerprint);
        Assert.Equal(Commit, receipt.InstalledCommit);
        Assert.Equal(Commit, receipt.LastObservedCommit);
        Assert.Equal("fixture-package", receipt.ManifestName);
        Assert.Equal(ManagedPluginSourceValidator.CodexFormat, receipt.ManifestFormat);
        Assert.Equal(ManagedPluginSourceValidator.CodexFormat, receipt.SourceFormat);
        Assert.NotNull(await store.GetRejectionAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Last_observed_commit_changes_without_replacing_the_installed_commit()
    {
        var paths = _paths;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(paths, time);
        var source = Source();
        await store.SaveReceiptAsync(source, Candidate(Commit, "1.0.0"), TestContext.Current.CancellationToken);
        var installed = await store.GetReceiptAsync(source.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(installed);

        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.UpdateLastObservedCommitAsync(
            source.Id, LaterCommit, TestContext.Current.CancellationToken));

        var observed = await store.GetReceiptAsync(source.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(observed);
        Assert.Equal(Commit, observed.InstalledCommit);
        Assert.Equal(LaterCommit, observed.LastObservedCommit);
        Assert.Equal(installed.InstalledAt, observed.InstalledAt);
        Assert.False(await store.UpdateLastObservedCommitAsync(
            "missing", LaterCommit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveSourcesExcept_removes_rejection_only_sources()
    {
        var paths = _paths;
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "bad content", false,
            TestContext.Current.CancellationToken);

        await store.RemoveSourcesExceptAsync([], TestContext.Current.CancellationToken);

        Assert.Null(await store.GetRejectionAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveRejection_sanitizes_and_limits_the_durable_reason()
    {
        var paths = _paths;
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        var reason = "bad\r\n" + new string('x', ManagedPluginStateStore.MaximumRejectionReasonLength + 100);

        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, reason, true, TestContext.Current.CancellationToken);

        var rejection = await store.GetRejectionAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken);
        Assert.NotNull(rejection);
        Assert.Equal(ManagedPluginStateStore.MaximumRejectionReasonLength, rejection.Reason.Length);
        Assert.DoesNotContain('\r', rejection.Reason);
        Assert.DoesNotContain('\n', rejection.Reason);
    }

    [Fact]
    public async Task SaveRejection_preserves_a_later_security_classification()
    {
        var paths = _paths;
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "invalid metadata", false,
            TestContext.Current.CancellationToken);

        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "security rejection", true,
            TestContext.Current.CancellationToken);

        var rejection = await store.GetRejectionAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken);
        Assert.NotNull(rejection);
        Assert.True(rejection.SecurityRejection);
        Assert.Equal("security rejection", rejection.Reason);
    }

    [Fact]
    public async Task Security_alert_claim_succeeds_once_and_rejects_nonsecurity_records()
    {
        var paths = _paths;
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "invalid metadata", false,
            TestContext.Current.CancellationToken);

        Assert.False(await store.TryClaimSecurityAlertAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken));

        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "security rejection", true,
            TestContext.Current.CancellationToken);
        Assert.True(await store.TryClaimSecurityAlertAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken));
        Assert.False(await store.TryClaimSecurityAlertAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken));

        var rejection = await store.GetRejectionAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken);
        Assert.NotNull(rejection);
        Assert.True(rejection.AlertEmitted);
    }

    [Fact]
    public async Task Retry_receipt_and_rejection_delete_use_one_transaction()
    {
        await MigrateAsync();
        var store = new ManagedPluginStateStore(_paths, TimeProvider.System);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Id, fingerprint, Commit, "security rejection", true,
            TestContext.Current.CancellationToken);
        await CreateRejectDeleteTriggerAsync();

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveReceiptAfterRetryAsync(
            source, Candidate(Commit, "1.0.0"), TestContext.Current.CancellationToken));

        Assert.Null(await store.GetReceiptAsync(source.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.GetRejectionAsync(
            source.Id, fingerprint, Commit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retry_observed_commit_and_rejection_delete_use_one_transaction()
    {
        await MigrateAsync();
        var store = new ManagedPluginStateStore(_paths, TimeProvider.System);
        var source = Source();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        await store.SaveReceiptAsync(source, Candidate(Commit, "1.0.0"), TestContext.Current.CancellationToken);
        await store.SaveRejectionAsync(
            source.Id, fingerprint, LaterCommit, "security rejection", true,
            TestContext.Current.CancellationToken);
        await CreateRejectDeleteTriggerAsync();

        await Assert.ThrowsAsync<SqliteException>(() => store.UpdateLastObservedCommitAfterRetryAsync(
            source.Id, fingerprint, LaterCommit, TestContext.Current.CancellationToken));

        var receipt = await store.GetReceiptAsync(source.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal(Commit, receipt.LastObservedCommit);
        Assert.NotNull(await store.GetRejectionAsync(
            source.Id, fingerprint, LaterCommit, TestContext.Current.CancellationToken));
    }

    private async Task MigrateAsync()
        => await new SchemaMigrator(_paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(_paths.SqliteDbPath, TestContext.Current.CancellationToken);

    private async Task CreateRejectDeleteTriggerAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TRIGGER reject_plugin_rejection_delete
            BEFORE DELETE ON managed_plugin_rejections
            BEGIN
                SELECT RAISE(ABORT, 'delete blocked');
            END;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static ManagedPluginSource Source() => new()
    {
        Id = "fixture",
        Repository = "owner/repository",
        Format = "codex",
        ReferenceKind = ManagedPluginReferenceKind.Branch,
        Reference = "main",
    };

    private static ManagedPluginCandidate Candidate(string commit, string? version) => new(
        commit,
        ManagedPluginSourceValidator.CodexFormat,
        "fixture-package",
        version,
        "unused",
        [],
        []);
}
