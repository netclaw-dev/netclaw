// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginSyncServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class GitSkillPluginSyncServiceTests : IDisposable
{
    private const string FirstCommit = "13e26d39ed01d97ea592235d041304d289f4ba07";
    private const string SecondCommit = "23e26d39ed01d97ea592235d041304d289f4ba08";
    private readonly DisposableTempDir _temp = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new();

    public GitSkillPluginSyncServiceTests()
    {
        _paths = new NetclawPaths(_temp.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        SqliteTestPools.Clear(_paths);
        _temp.Dispose();
    }

    [Fact]
    public async Task First_install_publishes_all_skills_in_one_inventory_snapshot()
    {
        var source = Source();
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var registry = new SkillRegistry();
        var publicationCount = 0;
        var refresher = CreateRefresher(registry, () => publicationCount++);
        var service = await CreateServiceAsync(source, refresher, acquirer, new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, publicationCount);
        var plugin = Assert.Single(result.Sources);
        Assert.Equal(SkillSyncResult.GitPluginSourceKind, plugin.SourceKind);
        Assert.Equal(FirstCommit, plugin.Commit);
        Assert.NotNull(registry.GetByName("plugin-skill"));
    }

    [Fact]
    public async Task Branch_update_keeps_prior_directory_and_publishes_new_files()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var first = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill", "old text");
        var service = await CreateServiceAsync(source, refresher, first, new RecordingSink());
        await service.SyncAsync(TestContext.Current.CancellationToken);
        var oldPath = registry.GetByName("plugin-skill")!.FilePath;

        var second = new FakeAcquirer(_paths, source, SecondCommit, "2.0.0", "plugin-skill", "new text");
        var nextService = await CreateServiceAsync(source, refresher, second, new RecordingSink());
        await nextService.SyncAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(oldPath));
        Assert.Contains("old text", await File.ReadAllTextAsync(oldPath, TestContext.Current.CancellationToken));
        Assert.Contains("new text", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Interleaved_refresh_keeps_the_prior_snapshot_until_receipt_publication()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var first = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill", "old text");
        await (await CreateServiceAsync(source, refresher, first, new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);
        var oldPath = registry.GetByName("plugin-skill")!.FilePath;
        string? pathDuringInterleave = null;
        string? contentDuringInterleave = null;

        var second = new FakeAcquirer(_paths, source, SecondCommit, "2.0.0", "plugin-skill", "new text")
        {
            BeforeReturn = () =>
            {
                refresher.Refresh();
                pathDuringInterleave = registry.GetByName("plugin-skill")!.FilePath;
                contentDuringInterleave = File.ReadAllText(pathDuringInterleave);
            },
        };

        await (await CreateServiceAsync(source, refresher, second, new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(oldPath, pathDuringInterleave);
        Assert.Contains("old text", contentDuringInterleave);
        Assert.Contains("new text", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Receipt_persistence_failure_keeps_the_prior_receipt_and_registry()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var first = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill", "old text");
        await (await CreateServiceAsync(source, refresher, first, new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);
        var oldPath = registry.GetByName("plugin-skill")!.FilePath;

        await AddReceiptFailureTriggerAsync();
        var second = new FakeAcquirer(_paths, source, SecondCommit, "2.0.0", "plugin-skill", "new text");
        var result = await (await CreateServiceAsync(source, refresher, second, new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        var receipt = await new ManagedPluginStateStore(_paths, _time).GetReceiptAsync(
            source.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal(FirstCommit, receipt.InstalledCommit);
        Assert.Equal(oldPath, registry.GetByName("plugin-skill")!.FilePath);
        Assert.Contains("old text", await File.ReadAllTextAsync(oldPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Equal_version_records_last_observed_commit_without_a_second_fetch()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var first = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var service = await CreateServiceAsync(source, refresher, first, new RecordingSink());
        await service.SyncAsync(TestContext.Current.CancellationToken);

        var next = new FakeAcquirer(_paths, source, SecondCommit, "1.0.0", "plugin-skill");
        var nextService = await CreateServiceAsync(source, refresher, next, new RecordingSink());
        await nextService.SyncAsync(TestContext.Current.CancellationToken);
        await nextService.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, next.AcquireCount);
        var receipt = await new ManagedPluginStateStore(_paths, _time).GetReceiptAsync(
            source.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal(FirstCommit, receipt.InstalledCommit);
        Assert.Equal(SecondCommit, receipt.LastObservedCommit);
        Assert.False(Directory.Exists(_paths.ManagedGitSkillCommitDirectory(
            source.Id, ManagedPluginSourceValidator.Fingerprint(source), SecondCommit)));
    }

    [Fact]
    public async Task Commit_pin_does_not_resolve_and_known_rejection_survives_a_new_service()
    {
        var source = Source();
        source.ReferenceKind = ManagedPluginReferenceKind.Commit;
        source.Reference = FirstCommit.ToUpperInvariant();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var store = await CreateStoreAsync();
        await store.SaveRejectionAsync(
            source.Id,
            ManagedPluginSourceValidator.Fingerprint(source),
            FirstCommit,
            "unsafe plugin",
            false,
            TestContext.Current.CancellationToken);
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill")
        {
            FailOnResolve = true,
        };
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] },
            _paths,
            refresher,
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            acquirer,
            new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, acquirer.ResolveCount);
        Assert.Equal(0, acquirer.AcquireCount);
        Assert.Equal(1, Assert.Single(result.Sources).RejectedCount);
    }

    [Fact]
    public async Task Security_rejection_persists_and_emits_one_claimed_alert()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill")
        {
            Rejection = new GitSkillPluginRejectedException(FirstCommit, "scanner result", true),
        };
        var alerts = new RecordingSink();
        var service = await CreateServiceAsync(source, refresher, acquirer, alerts);

        await service.SyncAsync(TestContext.Current.CancellationToken);
        await service.SyncAsync(TestContext.Current.CancellationToken);

        var alert = Assert.Single(alerts.Alerts);
        Assert.Equal("skill.plugin.security_rejected", alert.Type);
        Assert.Equal(AlertType.SkillPluginSecurityRejected, alert.Category);
        Assert.DoesNotContain("scanner result", alert.Summary);
    }

    [Fact]
    public async Task Restart_claims_and_emits_a_persisted_security_rejection_alert()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        await store.SaveRejectionAsync(
            source.Id, fingerprint, FirstCommit, "scanner result", true, TestContext.Current.CancellationToken);
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var alerts = new RecordingSink();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] }, _paths, CreateRefresher(new SkillRegistry(), static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer, alerts);

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, acquirer.AcquireCount);
        Assert.Equal("skill.plugin.security_rejected", Assert.Single(alerts.Alerts).Type);
        Assert.True((await store.GetRejectionAsync(
            source.Id, fingerprint, FirstCommit, TestContext.Current.CancellationToken))!.AlertEmitted);
    }

    [Fact]
    public async Task Source_timeout_covers_branch_resolution_before_archive_acquisition()
    {
        var source = Source();
        source.TimeoutSeconds = 1;
        var acquirer = new BlockingResolveAcquirer();
        var service = await CreateServiceAsync(
            source, CreateRefresher(new SkillRegistry(), static () => { }), acquirer, new RecordingSink());

        var sync = service.SyncAsync(TestContext.Current.CancellationToken);
        await acquirer.ResolutionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = await sync;

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        Assert.Equal(0, acquirer.AcquireCount);
    }

    [Fact]
    public async Task Startup_cleanup_removes_staging_and_orphan_commits_but_keeps_receipt_directory()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, ReceiptCandidate(FirstCommit, "1.0.0"), TestContext.Current.CancellationToken);
        var fingerprint = ManagedPluginSourceValidator.Fingerprint(source);
        var selected = _paths.ManagedGitSkillCommitDirectory(source.Id, fingerprint, FirstCommit);
        var orphan = _paths.ManagedGitSkillCommitDirectory(source.Id, fingerprint, SecondCommit);
        var staging = Path.Combine(_paths.ManagedGitSkillDirectory(source.Id), ".staging", "candidate");
        var publishedStagingResource = Path.Combine(selected, "plugin-skill", "resources", ".staging", "guide.md");
        var publishedCommitsResource = Path.Combine(selected, "plugin-skill", "resources", "commits", "guide.md");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.GetDirectoryName(publishedStagingResource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publishedCommitsResource)!);
        File.WriteAllText(publishedStagingResource, "published staging resource");
        File.WriteAllText(publishedCommitsResource, "published commits resource");
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] },
            _paths,
            CreateRefresher(registry, static () => { }),
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill"),
            new RecordingSink());

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(selected));
        Assert.False(Directory.Exists(orphan));
        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(publishedStagingResource));
        Assert.True(File.Exists(publishedCommitsResource));
    }

    [Fact]
    public async Task Disabled_source_does_not_publish_a_prior_receipt()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, ReceiptCandidate(FirstCommit, "1.0.0"), TestContext.Current.CancellationToken);
        var directory = _paths.ManagedGitSkillCommitDirectory(
            source.Id, ManagedPluginSourceValidator.Fingerprint(source), FirstCommit);
        Directory.CreateDirectory(Path.Combine(directory, "plugin-skill"));
        File.WriteAllText(Path.Combine(directory, "plugin-skill", "SKILL.md"), SkillMarkdown("plugin-skill", "hidden"));
        source.Enabled = false;
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill");
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] }, _paths, CreateRefresher(registry, static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer,
            new RecordingSink());

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, acquirer.ResolveCount);
        Assert.Null(registry.GetByName("plugin-skill"));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task Changed_source_acquisition_failure_keeps_the_prior_receipt_and_directory()
    {
        var original = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(original, ReceiptCandidate(FirstCommit, "1.0.0"), TestContext.Current.CancellationToken);
        var oldDirectory = _paths.ManagedGitSkillCommitDirectory(
            original.Id, ManagedPluginSourceValidator.Fingerprint(original), FirstCommit);
        var oldSkillPath = Path.Combine(oldDirectory, "plugin-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(oldSkillPath)!);
        File.WriteAllText(oldSkillPath, SkillMarkdown("plugin-skill", "prior package"));
        var changed = Source();
        changed.Reference = "release";
        var acquirer = new FakeAcquirer(_paths, changed, SecondCommit, "2.0.0", "plugin-skill")
        {
            Failure = new IOException("network unavailable"),
        };
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [changed] }, _paths, CreateRefresher(registry, static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer,
            new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        Assert.Equal(FirstCommit, (await store.GetReceiptAsync(original.Id, TestContext.Current.CancellationToken))!.InstalledCommit);
        Assert.True(Directory.Exists(oldDirectory));
        Assert.Contains("prior package", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restart_publishes_the_installed_plugin_before_remote_work_finishes()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, ReceiptCandidate(FirstCommit, "1.0.0"), TestContext.Current.CancellationToken);
        var directory = _paths.ManagedGitSkillCommitDirectory(
            source.Id, ManagedPluginSourceValidator.Fingerprint(source), FirstCommit);
        var skillPath = Path.Combine(directory, "plugin-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        File.WriteAllText(skillPath, SkillMarkdown("plugin-skill", "installed package"));
        var acquirer = new BlockingResolveAcquirer();
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] }, _paths, CreateRefresher(registry, static () => { }), _time,
            new NoOpSkillContentScanner(), NullLogger<ServerFeedSkillSyncService>.Instance, store, acquirer,
            new RecordingSink());

        var sync = service.SyncAsync(TestContext.Current.CancellationToken);
        await acquirer.ResolutionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(registry.GetByName("plugin-skill"));

        _time.Advance(TimeSpan.FromSeconds(source.TimeoutSeconds));
        await sync;
    }

    [Fact]
    public async Task Invalid_plugin_configuration_reports_failure_and_keeps_installed_content()
    {
        var source = Source();
        var store = await CreateStoreAsync();
        await store.SaveReceiptAsync(source, ReceiptCandidate(FirstCommit, "1.0.0"), TestContext.Current.CancellationToken);
        var directory = _paths.ManagedGitSkillCommitDirectory(
            source.Id, ManagedPluginSourceValidator.Fingerprint(source), FirstCommit);
        var skillPath = Path.Combine(directory, "plugin-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        File.WriteAllText(skillPath, SkillMarkdown("plugin-skill", "installed package"));
        var duplicate = Source();
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source, duplicate] }, _paths,
            CreateRefresher(registry, static () => { }), _time, new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance, store,
            new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill"), new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        var failure = Assert.Single(result.Sources);
        Assert.Equal("managed-git-plugins", failure.Name);
        Assert.Equal(SkillSyncResult.GitPluginSourceKind, failure.SourceKind);
        Assert.Equal(1, failure.FailedCount);
        Assert.NotNull(registry.GetByName("plugin-skill"));
    }

    [Fact]
    public async Task Scanner_service_failure_creates_no_rejection_or_alert()
    {
        var source = Source();
        var acquirer = new FakeAcquirer(_paths, source, FirstCommit, "1.0.0", "plugin-skill")
        {
            Failure = new GitSkillPluginScannerUnavailableException("scanner unavailable"),
        };
        var alerts = new RecordingSink();
        var service = await CreateServiceAsync(source, CreateRefresher(new SkillRegistry(), static () => { }), acquirer, alerts);

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);
        var store = new ManagedPluginStateStore(_paths, _time);

        Assert.Equal(1, Assert.Single(result.Sources).FailedCount);
        Assert.Null(await store.GetRejectionAsync(
            source.Id, ManagedPluginSourceValidator.Fingerprint(source), FirstCommit,
            TestContext.Current.CancellationToken));
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task Versionless_commit_change_publishes_the_new_commit()
    {
        var source = Source();
        var registry = new SkillRegistry();
        var refresher = CreateRefresher(registry, static () => { });
        await (await CreateServiceAsync(
            source, refresher, new FakeAcquirer(_paths, source, FirstCommit, null, "plugin-skill"), new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);
        var updated = new FakeAcquirer(_paths, source, SecondCommit, null, "plugin-skill", "new versionless content");
        await (await CreateServiceAsync(source, refresher, updated, new RecordingSink()))
            .SyncAsync(TestContext.Current.CancellationToken);

        var receipt = await new ManagedPluginStateStore(_paths, _time).GetReceiptAsync(
            source.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal(SecondCommit, receipt.InstalledCommit);
        Assert.Equal(1, updated.AcquireCount);
        Assert.Contains("new versionless content", await File.ReadAllTextAsync(
            registry.GetByName("plugin-skill")!.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_failed_plugin_does_not_block_a_successful_plugin()
    {
        var failed = Source();
        failed.Id = "failed";
        var healthy = Source();
        healthy.Id = "healthy";
        var store = await CreateStoreAsync();
        var registry = new SkillRegistry();
        var service = new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [failed, healthy] },
            _paths,
            CreateRefresher(registry, static () => { }),
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            new TwoPluginAcquirer(_paths, failed, healthy),
            new RecordingSink());

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(result.Sources, row => row.Name == "failed").FailedCount);
        Assert.Equal(1, Assert.Single(result.Sources, row => row.Name == "healthy").ChangedCount);
        Assert.NotNull(registry.GetByName("healthy-skill"));
    }

    private async Task<ManagedPluginStateStore> CreateStoreAsync()
    {
        await new SchemaMigrator(_paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(_paths.SqliteDbPath, TestContext.Current.CancellationToken);
        return new ManagedPluginStateStore(_paths, _time);
    }

    private async Task AddReceiptFailureTriggerAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_paths.SqliteDbPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TRIGGER reject_managed_plugin_receipt_update
            BEFORE UPDATE ON managed_plugin_receipts
            BEGIN
                SELECT RAISE(ABORT, 'receipt persistence failed');
            END;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ServerFeedSkillSyncService> CreateServiceAsync(
        ManagedPluginSource source,
        SkillInventoryRefresher refresher,
        IGitSkillPluginAcquirer acquirer,
        RecordingSink sink)
    {
        var store = await CreateStoreAsync();
        return new ServerFeedSkillSyncService(
            new SkillFeedsConfig { Plugins = [source] },
            _paths,
            refresher,
            _time,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            store,
            acquirer,
            sink);
    }

    private SkillInventoryRefresher CreateRefresher(SkillRegistry registry, Action publish)
        => new(
            _paths,
            new SkillFeedsConfig(),
            [],
            registry,
            new SkillIndexPublisher(registry, new SkillIndexContextLayer(), (_, _) =>
            {
                publish();
                return true;
            }));

    private static ManagedPluginSource Source() => new()
    {
        Id = "fixture",
        Repository = "owner/repository",
        Format = "codex",
        ReferenceKind = ManagedPluginReferenceKind.Branch,
        Reference = "main",
    };

    private static ManagedPluginCandidate ReceiptCandidate(string commit, string? version) => new(
        commit,
        ManagedPluginSourceValidator.CodexFormat,
        "fixture-package",
        version,
        "unused",
        [],
        []);

    private sealed class FakeAcquirer(
        NetclawPaths paths,
        ManagedPluginSource source,
        string commit,
        string? version,
        string skillName,
        string description = "plugin guidance") : IGitSkillPluginAcquirer
    {
        public bool FailOnResolve { get; init; }
        public GitSkillPluginRejectedException? Rejection { get; init; }
        public Exception? Failure { get; init; }
        public Action? BeforeReturn { get; init; }
        public int ResolveCount { get; private set; }
        public int AcquireCount { get; private set; }

        public Task<string> ResolveDefaultBranchAsync(
            string repository,
            CancellationToken cancellationToken) => Task.FromResult("main");

        public Task<ManagedPluginCandidate> AcquireAsync(
            ManagedPluginSource ignored,
            CancellationToken cancellationToken)
            => AcquireAsync(ignored, commit, cancellationToken);

        public Task<string> ResolveCommitAsync(
            ManagedPluginSource ignored,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            if (FailOnResolve)
                throw new InvalidOperationException("The commit pin must not resolve.");
            return Task.FromResult(commit);
        }

        public Task<ManagedPluginCandidate> AcquireAsync(
            ManagedPluginSource ignored,
            string resolvedCommit,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            if (Failure is not null)
                throw Failure;
            if (Rejection is not null)
                throw Rejection;

            var directory = paths.ManagedGitSkillCommitDirectory(
                source.Id,
                ManagedPluginSourceValidator.Fingerprint(source),
                resolvedCommit);
            var skillDirectory = Path.Combine(directory, skillName);
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), $$"""
                ---
                name: {{skillName}}
                description: {{description}}
                metadata:
                  version: {{version ?? ""}}
                ---

                # {{skillName}}
                """);
            BeforeReturn?.Invoke();
            return Task.FromResult(new ManagedPluginCandidate(
                resolvedCommit,
                ManagedPluginSourceValidator.CodexFormat,
                skillName,
                version,
                directory,
                SkillScanner.Scan(directory).AcceptedSkills,
                []));
        }
    }

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }

    private sealed class TwoPluginAcquirer(NetclawPaths paths, ManagedPluginSource failed, ManagedPluginSource healthy)
        : IGitSkillPluginAcquirer
    {
        public Task<string> ResolveDefaultBranchAsync(
            string repository,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ManagedPluginCandidate> AcquireAsync(ManagedPluginSource source, CancellationToken cancellationToken)
            => AcquireAsync(source, source.Id == failed.Id ? FirstCommit : SecondCommit, cancellationToken);

        public Task<string> ResolveCommitAsync(ManagedPluginSource source, CancellationToken cancellationToken)
            => Task.FromResult(source.Id == failed.Id ? FirstCommit : SecondCommit);

        public Task<ManagedPluginCandidate> AcquireAsync(
            ManagedPluginSource source,
            string commit,
            CancellationToken cancellationToken)
        {
            if (source.Id == failed.Id)
                throw new IOException("network unavailable");

            var directory = paths.ManagedGitSkillCommitDirectory(
                healthy.Id, ManagedPluginSourceValidator.Fingerprint(healthy), commit);
            var skillDirectory = Path.Combine(directory, "healthy-skill");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), SkillMarkdown("healthy-skill", "healthy"));
            return Task.FromResult(new ManagedPluginCandidate(
                commit,
                ManagedPluginSourceValidator.CodexFormat,
                "healthy-plugin",
                null,
                directory,
                SkillScanner.Scan(directory).AcceptedSkills,
                []));
        }
    }

    private sealed class BlockingResolveAcquirer : IGitSkillPluginAcquirer
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResolutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcquireCount { get; private set; }

        public Task<string> ResolveDefaultBranchAsync(
            string repository,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ManagedPluginCandidate> AcquireAsync(ManagedPluginSource source, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public async Task<string> ResolveCommitAsync(ManagedPluginSource source, CancellationToken cancellationToken)
        {
            ResolutionStarted.TrySetResult();
            await _never.Task.WaitAsync(cancellationToken);
            return "";
        }

        public Task<ManagedPluginCandidate> AcquireAsync(
            ManagedPluginSource source,
            string commit,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            throw new NotSupportedException();
        }
    }

    private static string SkillMarkdown(string name, string description) => $$"""
        ---
        name: {{name}}
        description: {{description}}
        ---

        # {{name}}
        """;
}
