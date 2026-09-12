// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginStateStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class GitSkillPluginStateStoreTests : IDisposable
{
    private const string Commit = "13e26d39ed01d97ea592235d041304d289f4ba07";
    private const string LaterCommit = "23e26d39ed01d97ea592235d041304d289f4ba08";
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Migration_and_store_preserve_receipts_and_scope_rejections_by_fingerprint()
    {
        var paths = new NetclawPaths(_temp.Path);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var migrator = new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance);
        await migrator.MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(paths, time);
        var source = Source();
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);

        Assert.True(await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, "bad content", true,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.GetRejectionAsync(
            source.Name, new string('a', 64), Commit,
            TestContext.Current.CancellationToken));

        await store.SaveReceiptAsync(source, Commit, "1.0.0", TestContext.Current.CancellationToken);

        var receipt = Assert.Single(await store.LoadReceiptsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fingerprint, receipt.SourceFingerprint);
        Assert.Equal(Commit, receipt.InstalledCommit);
        Assert.Equal(Commit, receipt.LastObservedCommit);
        Assert.NotNull(await store.GetRejectionAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Last_observed_commit_changes_without_replacing_the_installed_commit()
    {
        var paths = new NetclawPaths(_temp.Path);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(paths, time);
        var source = Source();
        await store.SaveReceiptAsync(source, Commit, "1.0.0", TestContext.Current.CancellationToken);
        var installed = await store.GetReceiptAsync(source.Name, TestContext.Current.CancellationToken);
        Assert.NotNull(installed);

        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.UpdateLastObservedCommitAsync(
            source.Name, LaterCommit, TestContext.Current.CancellationToken));

        var observed = await store.GetReceiptAsync(source.Name, TestContext.Current.CancellationToken);
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
        var paths = new NetclawPaths(_temp.Path);
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, "bad content", false,
            TestContext.Current.CancellationToken);

        await store.RemoveSourcesExceptAsync([], TestContext.Current.CancellationToken);

        Assert.Null(await store.GetRejectionAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveRejection_sanitizes_and_limits_the_durable_reason()
    {
        var paths = new NetclawPaths(_temp.Path);
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        var reason = "bad\r\n" + new string('x', GitSkillPluginStateStore.MaximumRejectionReasonLength + 100);

        await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, reason, true, TestContext.Current.CancellationToken);

        var rejection = await store.GetRejectionAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken);
        Assert.NotNull(rejection);
        Assert.Equal(GitSkillPluginStateStore.MaximumRejectionReasonLength, rejection.Reason.Length);
        Assert.DoesNotContain('\r', rejection.Reason);
        Assert.DoesNotContain('\n', rejection.Reason);
    }

    [Fact]
    public async Task SaveRejection_preserves_a_later_security_classification()
    {
        var paths = new NetclawPaths(_temp.Path);
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, "invalid metadata", false,
            TestContext.Current.CancellationToken);

        await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, "security rejection", true,
            TestContext.Current.CancellationToken);

        var rejection = await store.GetRejectionAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken);
        Assert.NotNull(rejection);
        Assert.True(rejection.SecurityRejection);
        Assert.Equal("security rejection", rejection.Reason);
    }

    [Fact]
    public async Task Security_alert_claim_succeeds_once_and_rejects_nonsecurity_records()
    {
        var paths = new NetclawPaths(_temp.Path);
        await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var store = new GitSkillPluginStateStore(paths, TimeProvider.System);
        var source = Source();
        var fingerprint = GitSkillPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, "invalid metadata", false,
            TestContext.Current.CancellationToken);

        Assert.False(await store.TryClaimSecurityAlertAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken));

        await store.SaveRejectionAsync(
            source.Name, fingerprint, Commit, "security rejection", true,
            TestContext.Current.CancellationToken);
        Assert.True(await store.TryClaimSecurityAlertAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken));
        Assert.False(await store.TryClaimSecurityAlertAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken));

        var rejection = await store.GetRejectionAsync(
            source.Name, fingerprint, Commit, TestContext.Current.CancellationToken);
        Assert.NotNull(rejection);
        Assert.True(rejection.AlertEmitted);
    }

    private static GitSkillPluginSource Source() => new()
    {
        Name = "fixture",
        Repository = "owner/repository",
        Format = "codex",
        ReferenceKind = GitSkillPluginReferenceKind.Branch,
        Reference = "main",
    };
}
