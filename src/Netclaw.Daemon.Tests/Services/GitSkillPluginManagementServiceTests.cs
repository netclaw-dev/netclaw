// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginManagementServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class GitSkillPluginManagementServiceTests : IDisposable
{
    private const string Commit = "13e26d39ed01d97ea592235d041304d289f4ba07";
    private readonly DisposableTempDir _temp = new();
    private readonly NetclawPaths _paths;

    public GitSkillPluginManagementServiceTests()
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
    public async Task Tag_install_resolves_one_commit_and_preserves_unrelated_configuration()
    {
        await File.WriteAllTextAsync(
            _paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "CustomSetting": { "keep": true },
              "SkillFeeds": {
                "SyncIntervalMinutes": 15,
                "Feeds": [
                  { "Name": "team", "Url": "https://skills.example/", "Enabled": true }
                ]
              }
            }
            """,
            TestContext.Current.CancellationToken);
        var acquirer = new FakeAcquirer { Commit = Commit };
        var service = CreateService(acquirer);

        var install = await service.InstallAsync(
            new ManagedPluginApi.InstallRequest
            {
                Repository = "https://github.com/owner/repository.git",
                ReferenceKind = ManagedPluginApi.InstallReferenceKind.Tag,
                Reference = "v1.2.0",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagedPluginReferenceKind.Commit, install.Source.ReferenceKind);
        Assert.Equal(Commit, install.Source.Reference);
        Assert.Equal(1, acquirer.ResolveCommitCount);
        Assert.Equal(0, acquirer.AcquireCount);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(
            _paths.NetclawConfigPath,
            TestContext.Current.CancellationToken))!.AsObject();
        Assert.True(root["CustomSetting"]!["keep"]!.GetValue<bool>());
        Assert.Equal("team", root["SkillFeeds"]!["Feeds"]![0]!["Name"]!.GetValue<string>());
        var plugin = Assert.Single(root["SkillFeeds"]!["Plugins"]!.AsArray())!.AsObject();
        Assert.Equal("repository", plugin["Id"]!.GetValue<string>());
        Assert.Equal("owner/repository", plugin["Repository"]!.GetValue<string>());
        Assert.Equal("Commit", plugin["ReferenceKind"]!.GetValue<string>());
        Assert.Equal(Commit, plugin["Reference"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_tag_resolution_does_not_persist_the_source()
    {
        await File.WriteAllTextAsync(
            _paths.NetclawConfigPath,
            "{ \"configVersion\": 1, \"Marker\": \"keep\" }",
            TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(
            _paths.NetclawConfigPath,
            TestContext.Current.CancellationToken);
        var service = CreateService(new FakeAcquirer { ResolveError = new HttpRequestException("missing tag") });

        await Assert.ThrowsAsync<HttpRequestException>(() => service.InstallAsync(
            new ManagedPluginApi.InstallRequest
            {
                Repository = "owner/repository",
                ReferenceKind = ManagedPluginApi.InstallReferenceKind.Tag,
                Reference = "missing",
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(before, await File.ReadAllTextAsync(
            _paths.NetclawConfigPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Default_branch_install_persists_the_resolved_branch_without_acquisition()
    {
        var acquirer = new FakeAcquirer { DefaultBranch = "trunk" };
        var service = CreateService(acquirer);

        var install = await service.InstallAsync(
            new ManagedPluginApi.InstallRequest
            {
                Repository = "owner/repository",
                ReferenceKind = ManagedPluginApi.InstallReferenceKind.DefaultBranch,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagedPluginReferenceKind.Branch, install.Source.ReferenceKind);
        Assert.Equal("trunk", install.Source.Reference);
        Assert.Equal(1, acquirer.ResolveDefaultBranchCount);
        Assert.Equal(0, acquirer.AcquireCount);
        Assert.True(File.Exists(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Explicit_commit_install_persists_the_canonical_lowercase_identity()
    {
        var service = CreateService(new FakeAcquirer());

        var install = await service.InstallAsync(
            new ManagedPluginApi.InstallRequest
            {
                Repository = "owner/repository",
                ReferenceKind = ManagedPluginApi.InstallReferenceKind.Commit,
                Reference = Commit.ToUpperInvariant(),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(Commit, install.Source.Reference);
    }

    [Fact]
    public async Task List_reports_not_installed_when_the_receipt_directory_is_missing()
    {
        await new SchemaMigrator(_paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(_paths.SqliteDbPath, TestContext.Current.CancellationToken);
        var source = new ManagedPluginSource
        {
            Id = "fixture",
            Repository = "owner/repository",
            Format = "codex",
            ReferenceKind = ManagedPluginReferenceKind.Commit,
            Reference = Commit,
        };
        await new ManagedPluginStateStore(_paths, new FakeTimeProvider()).SaveReceiptAsync(
            source,
            new ManagedPluginCandidate(
                Commit,
                ManagedPluginSourceValidator.CodexFormat,
                "fixture-package",
                "1.0.0",
                "unused",
                [],
                []),
            TestContext.Current.CancellationToken);
        var service = CreateService(
            new FakeAcquirer(),
            new SkillFeedsConfig { Plugins = [source] });

        var result = await service.ListAsync(TestContext.Current.CancellationToken);

        var plugin = Assert.Single(result.Plugins);
        Assert.Equal(ManagedPluginApi.PluginStatus.NotInstalled, plugin.Status);
        Assert.Equal("fixture", plugin.SourceId);
        Assert.Equal("fixture-package", plugin.ManifestName);
    }

    private GitSkillPluginManagementService CreateService(FakeAcquirer acquirer)
        => CreateService(acquirer, new SkillFeedsConfig());

    private GitSkillPluginManagementService CreateService(
        FakeAcquirer acquirer,
        SkillFeedsConfig feedsConfig)
        => new(
            feedsConfig,
            _paths,
            acquirer,
            new ManagedPluginStateStore(_paths, new FakeTimeProvider()),
            new GitSkillPluginConfigStore(_paths, new DaemonRestartSignal()),
            new FakeTimeProvider());

    private sealed class FakeAcquirer : IGitSkillPluginAcquirer
    {
        public string DefaultBranch { get; init; } = "main";
        public string Commit { get; init; } = GitSkillPluginManagementServiceTests.Commit;
        public Exception? ResolveError { get; init; }
        public int ResolveDefaultBranchCount { get; private set; }
        public int ResolveCommitCount { get; private set; }
        public int AcquireCount { get; private set; }

        public Task<string> ResolveDefaultBranchAsync(
            string repository,
            CancellationToken cancellationToken)
        {
            ResolveDefaultBranchCount++;
            if (ResolveError is not null)
                throw ResolveError;
            return Task.FromResult(DefaultBranch);
        }

        public Task<string> ResolveCommitAsync(
            ManagedPluginSource source,
            CancellationToken cancellationToken)
        {
            ResolveCommitCount++;
            if (ResolveError is not null)
                throw ResolveError;
            return Task.FromResult(Commit);
        }

        public Task<ManagedPluginCandidate> AcquireAsync(
            ManagedPluginSource source,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            throw new InvalidOperationException("Install must not acquire plugin content before persistence.");
        }

        public Task<ManagedPluginCandidate> AcquireAsync(
            ManagedPluginSource source,
            string commit,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            throw new InvalidOperationException("Install must not acquire plugin content before persistence.");
        }
    }
}
