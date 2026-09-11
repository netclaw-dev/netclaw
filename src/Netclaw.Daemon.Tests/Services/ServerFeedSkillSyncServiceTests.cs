// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;
using Netclaw.Tests.Utilities;
using Xunit;
using SkillScanResult = Netclaw.Security.Skills.SkillScanResult;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncServiceTests : IDisposable
{
    private const string BaseUrl = "https://skillserver.test/";

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry = new();
    private readonly SkillIndexContextLayer _skillIndexLayer = new();
    private readonly SkillIndexPublisher _skillIndexPublisher;

    public ServerFeedSkillSyncServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _skillIndexPublisher = new SkillIndexPublisher(
            _skillRegistry,
            _skillIndexLayer,
            static (_, _) => true);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task SyncAsync_overlapping_callers_join_one_pass()
    {
        var handler = new ControlledFeedHandler(holdIndex: true);
        var service = CreateControlledService(handler, syncIntervalMinutes: 0);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await handler.FirstIndexRequest.Task;

        var first = service.SyncAsync(TestContext.Current.CancellationToken);
        var second = service.SyncAsync(TestContext.Current.CancellationToken);
        handler.ReleaseIndex();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].PassId, results[1].PassId);
        Assert.Equal(1, handler.IndexRequestCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncAsync_logs_one_correlated_start_and_completion_for_joined_callers()
    {
        var logger = new CapturingLogger();
        var handler = new ControlledFeedHandler(holdIndex: true);
        var service = CreateControlledService(handler, syncIntervalMinutes: 0, logger);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await handler.FirstIndexRequest.Task;

        var first = service.SyncAsync(TestContext.Current.CancellationToken);
        var second = service.SyncAsync(TestContext.Current.CancellationToken);
        handler.ReleaseIndex();

        var results = await Task.WhenAll(first, second);
        var passId = results[0].PassId;
        Assert.Equal(passId, results[1].PassId);
        Assert.Equal(1, logger.Entries.Count(entry => entry.Message.StartsWith("External skill sync pass started.", StringComparison.Ordinal)));
        Assert.Equal(1, logger.Entries.Count(entry => entry.Message.StartsWith("External skill sync pass completed.", StringComparison.Ordinal)));
        Assert.All(
            logger.Entries.Where(entry => entry.Message.StartsWith("External skill sync pass", StringComparison.Ordinal)),
            entry => Assert.Equal(passId, entry.Fields["PassId"]));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncAsync_canceled_wait_does_not_cancel_the_shared_pass()
    {
        var handler = new ControlledFeedHandler(holdIndex: true);
        var service = CreateControlledService(handler, syncIntervalMinutes: 0);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await handler.FirstIndexRequest.Task;

        using var canceledWait = new CancellationTokenSource();
        var canceled = service.SyncAsync(canceledWait.Token);
        var healthy = service.SyncAsync(TestContext.Current.CancellationToken);
        canceledWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        handler.ReleaseIndex();
        var result = await healthy;
        Assert.NotNull(result);
        Assert.Equal(1, handler.IndexRequestCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncAsync_rejects_calls_before_start_and_after_stop()
    {
        var handler = new ControlledFeedHandler(holdIndex: false);
        var service = CreateControlledService(handler, syncIntervalMinutes: 0);

        await Assert.ThrowsAsync<SkillSyncUnavailableException>(
            () => service.SyncAsync(TestContext.Current.CancellationToken));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.SyncAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SkillSyncUnavailableException>(
            () => service.SyncAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAsync_cancels_a_manual_pass_after_zero_interval_startup_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var scanner = new LifetimeScanner(blockOnScan: 2, observeCancellation: true);
        using var service = CreateLifecycleService(scanner, new FakeTimeProvider(), intervalMinutes: 0);
        await service.StartAsync(ct);
        await service.ExecuteTask!.WaitAsync(ct);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        var originalState = File.ReadAllBytes(_paths.ServerFeedSyncStatePath("team"));

        try
        {
            var manual = service.SyncAsync(ct);
            await scanner.Blocked.Task.WaitAsync(ct);
            await service.StopAsync(ct);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
            await scanner.Exited.Task.WaitAsync(ct);
            Assert.Equal(originalState, File.ReadAllBytes(_paths.ServerFeedSyncStatePath("team")));
        }
        finally
        {
            scanner.Release();
        }
    }

    [Fact]
    public async Task Dispose_cancels_active_pass_without_StopAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var scanner = new LifetimeScanner(blockOnScan: 1, observeCancellation: true);
        using var service = CreateLifecycleService(scanner, new FakeTimeProvider(), intervalMinutes: 0);
        await service.StartAsync(ct);
        await scanner.Blocked.Task.WaitAsync(ct);
        var manual = service.SyncAsync(ct);

        try
        {
            service.Dispose();

            await scanner.Exited.Task.WaitAsync(ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
            await service.ExecuteTask!.WaitAsync(ct);
            await Assert.ThrowsAsync<SkillSyncUnavailableException>(() => service.SyncAsync(ct));
            Assert.False(File.Exists(_paths.ServerFeedSyncStatePath("team")));
            Assert.Empty(_skillRegistry.GetAll());
        }
        finally
        {
            scanner.Release();
        }
    }

    [Fact]
    public async Task StopAsync_shutdown_timeout_logs_and_keeps_pass_token_valid_until_scanner_exits()
    {
        var ct = TestContext.Current.CancellationToken;
        var scanner = new LifetimeScanner(blockOnScan: 1, observeCancellation: false);
        var logger = new CapturingLogger();
        using var service = CreateLifecycleService(scanner, new FakeTimeProvider(), intervalMinutes: 0, logger);
        await service.StartAsync(ct);
        await scanner.Blocked.Task.WaitAsync(ct);
        var manual = service.SyncAsync(ct);
        using var stopBudget = new CancellationTokenSource();

        try
        {
            var stop = service.StopAsync(stopBudget.Token);
            stopBudget.Cancel();
            await stop;

            Assert.False(manual.IsCompleted);
            Assert.Contains(logger.Messages, message => message == "Host shutdown timed out while an external skill sync pass was still active.");
            // The fake delays cancellation acknowledgment. Its token must remain valid until it exits.
            Assert.True(scanner.LifetimeToken.WaitHandle.WaitOne(0));
            service.Dispose();
            Assert.True(scanner.LifetimeToken.WaitHandle.WaitOne(0));
            await Assert.ThrowsAsync<SkillSyncUnavailableException>(() => service.SyncAsync(ct));

            scanner.Release();
            await scanner.Exited.Task.WaitAsync(ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
            var canceledPass = Assert.Single(logger.Entries, entry => entry.Fields.TryGetValue("Outcome", out var outcome)
                && Equals(outcome, "canceled"));
            Assert.NotNull(canceledPass.Fields["PassId"]);
            Assert.False(File.Exists(_paths.ServerFeedSyncStatePath("team")));
            Assert.Empty(_skillRegistry.GetAll());
        }
        finally
        {
            scanner.Release();
        }
    }

    [Fact]
    public async Task ExecuteAsync_stops_normally_when_stop_precedes_scheduler_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new TimerSignalTimeProvider();
        var scanner = new LifetimeScanner(blockOnScan: 0, observeCancellation: true);
        using var service = CreateLifecycleService(scanner, time, intervalMinutes: 1);
        await service.StartAsync(ct);
        await time.TimerCreated.Task.WaitAsync(ct);

        // Cancel runs callbacks before StopAsync calls the base scheduler stop.
        // Force the due timer through that exact gap and await its normal completion.
        using var cancellation = scanner.LifetimeToken.Register(() =>
        {
            time.Advance(TimeSpan.FromMinutes(1));
            service.ExecuteTask!.WaitAsync(ct).GetAwaiter().GetResult();
        });

        await Task.Run(() => service.StopAsync(ct), ct);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SyncAsync_with_no_enabled_sources_returns_an_empty_successful_result()
    {
        var handler = new ControlledFeedHandler(holdIndex: false);
        var service = CreateControlledService(handler, syncIntervalMinutes: 0, enabled: false);
        await service.StartAsync(TestContext.Current.CancellationToken);

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Sources);
        Assert.True(result.Inventory.Succeeded);
        Assert.Equal(0, handler.IndexRequestCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncAsync_reports_a_failed_feed_and_a_healthy_feed()
    {
        var handler = new MultiFeedHandler();
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds =
            [
                new SkillFeedSource { Name = "healthy", Url = "https://healthy.test/", TimeoutSeconds = 30 },
                new SkillFeedSource { Name = "failed", Url = "https://failed.test/", TimeoutSeconds = 30 },
            ],
        };
        var service = CreateService(feeds, handler, new NoOpSkillContentScanner(), new FakeTimeProvider());
        await service.StartAsync(TestContext.Current.CancellationToken);

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        var healthy = Assert.Single(result.Sources, source => source.Name == "healthy");
        Assert.Equal(0, healthy.FailedCount);
        Assert.Equal("absent", healthy.Sidecar);
        var failed = Assert.Single(result.Sources, source => source.Name == "failed");
        Assert.Equal(1, failed.FailedCount);
        Assert.Equal("not-run", failed.Sidecar);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncAsync_reports_required_index_failure()
    {
        var handler = new MultiFeedHandler(failEveryFeed: true);
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds = [new SkillFeedSource { Name = "failed", Url = "https://failed.test/", TimeoutSeconds = 30 }],
        };
        var service = CreateService(feeds, handler, new NoOpSkillContentScanner(), new FakeTimeProvider());
        await service.StartAsync(TestContext.Current.CancellationToken);

        var result = await service.SyncAsync(TestContext.Current.CancellationToken);

        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.FailedCount);
        Assert.Equal("not-run", source.Sidecar);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_queue_a_duplicate_first_timer_tick()
    {
        var time = new TimerSignalTimeProvider();
        var handler = new ControlledFeedHandler(holdIndex: false);
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 1,
            Feeds = [new SkillFeedSource { Name = "team", Url = BaseUrl, TimeoutSeconds = 30 }],
        };
        var service = CreateService(feeds, handler, new NoOpSkillContentScanner(), time);
        await service.StartAsync(TestContext.Current.CancellationToken);
        var firstTimer = await time.TimerCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        // A recurring timer before the initial delay would retain a duplicate tick.
        Assert.Equal(Timeout.InfiniteTimeSpan, firstTimer.Period);
        Assert.Equal(TimeSpan.FromMinutes(1), firstTimer.DueTime);
        time.Advance(TimeSpan.FromMinutes(1));
        var periodicTimer = await time.PeriodicTimerCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(1), periodicTimer.Period);
        Assert.Equal(time.GetUtcNow(), periodicTimer.CreatedAt);
        await handler.SecondIndexRequest.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.IndexRequestCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExtractArchiveAsync_AllowsArbitraryResourcesAndPreservesExecutableMode()
    {
        var skillContent = Encoding.UTF8.GetBytes("""
            ---
            name: packaged
            description: Packaged skill.
            ---

            # Packaged
            """);
        var scriptContent = Encoding.UTF8.GetBytes("#!/bin/bash\necho ok\n");
        var binaryContent = new byte[] { 0x00, 0x01, 0xFF, 0x02 };
        var archive = BuildArchive(
            ("SKILL.md", skillContent, 0x1A4),
            ("tools/check", scriptContent, 0x1ED),
            ("assets/icon.bin", binaryContent, 0x1A4));

        var files = await CreateService().ExtractArchiveAsync(
            "packaged", "private", archive, TestContext.Current.CancellationToken);

        Assert.NotNull(files);
        Assert.Contains(files!, file => file.RelativePath == "tools/check" && file.UnixMode == 0x1ED);
        Assert.Contains(files, file => file.RelativePath == "assets/icon.bin");

        var feedDir = _paths.ServerFeedDirectory("private");
        await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
            feedDir, "packaged", files!, TestContext.Current.CancellationToken);

        var skillDir = Path.Combine(feedDir, "packaged");
        Assert.Equal(scriptContent, await File.ReadAllBytesAsync(Path.Combine(skillDir, "tools", "check"), TestContext.Current.CancellationToken));
        Assert.Equal(binaryContent, await File.ReadAllBytesAsync(Path.Combine(skillDir, "assets", "icon.bin"), TestContext.Current.CancellationToken));

        if (!OperatingSystem.IsWindows())
        {
            var mode = (int)File.GetUnixFileMode(Path.Combine(skillDir, "tools", "check")) & 0x1FF;
            Assert.Equal(0x1ED, mode);
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsTraversalEntries()
    {
        var skillContent = Encoding.UTF8.GetBytes("""
            ---
            name: packaged
            description: Packaged skill.
            ---

            # Packaged
            """);
        var archive = BuildArchive(
            ("SKILL.md", skillContent, 0x1A4),
            ("../escape.sh", Encoding.UTF8.GetBytes("echo no"), 0x1ED));

        var files = await CreateService().ExtractArchiveAsync(
            "packaged", "private", archive, TestContext.Current.CancellationToken);

        Assert.Null(files);
    }

    [Fact]
    public async Task SyncOnce_syncs_native_subagent_from_sidecar_after_empty_rfc_index()
    {
        var agentContent = AgentMarkdown("code-reviewer", "Managed reviewer", "Review code carefully. 请仔细审查代码。");
        var digest = SkillSyncHelpers.ComputeSha256(agentContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", agentContent, digest);

        var service = CreateService(handler);
        await RunSyncAsync(service);

        var agentPath = Path.Combine(_paths.ServerFeedAgentDirectory("team"), "code-reviewer.md");
        Assert.True(File.Exists(agentPath));
        Assert.Equal(agentContent, File.ReadAllText(agentPath));
        Assert.Equal(Encoding.UTF8.GetBytes(agentContent), await File.ReadAllBytesAsync(agentPath, TestContext.Current.CancellationToken));

        var state = ReadAgentSyncState();
        Assert.Equal("1.0.0", state.Skills["code-reviewer"].Version);
        Assert.Equal(digest, state.Skills["code-reviewer"].Sha256);
    }

    [Fact]
    public async Task SyncOnce_missing_native_sidecar_preserves_rfc_skill_sync()
    {
        var skillContent = "---\nname: feed-skill\ndescription: Feed skill\n---\n\n# Feed Skill\n";
        var digest = SkillSyncHelpers.ComputeSha256(skillContent);

        var handler = new FakeHttpMessageHandler();
        handler.AddStringResponse(
            BaseUrl + ".well-known/agent-skills/index.json",
            $$"""
            {
              "skills": [
                {
                  "name": "feed-skill",
                  "type": "skill",
                  "description": "Feed skill",
                  "url": "{{BaseUrl}}skills/feed-skill/1.0.0/SKILL.md",
                  "digest": "sha256:{{digest}}",
                  "version": "1.0.0"
                }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(BaseUrl + "skills/feed-skill/1.0.0/SKILL.md", skillContent, "text/markdown");
        handler.AddErrorResponse(BaseUrl + "subagents/v1/index.json", HttpStatusCode.NotFound);

        var service = CreateService(handler);
        await RunSyncAsync(service);

        var skillPath = Path.Combine(_paths.ServerFeedDirectory("team"), "feed-skill", "SKILL.md");
        Assert.True(File.Exists(skillPath));
        Assert.Equal(skillContent, File.ReadAllText(skillPath));
        Assert.False(Directory.Exists(_paths.ServerFeedAgentDirectory("team")));
    }

    [Fact]
    public async Task SyncOnce_digest_failure_keeps_existing_managed_subagents_and_skips_prune()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        var oldContent = AgentMarkdown("code-reviewer", "Old reviewer", "Old body.");
        File.WriteAllText(Path.Combine(agentDir, "code-reviewer.md"), oldContent);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["code-reviewer"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent)
                },
                ["stale-agent"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = "stale"
                }
            }
        });

        var expectedContent = AgentMarkdown("code-reviewer", "New reviewer", "New body.");
        var deliveredContent = AgentMarkdown("code-reviewer", "Tampered reviewer", "Tampered body.");
        var expectedDigest = SkillSyncHelpers.ComputeSha256(expectedContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", deliveredContent, expectedDigest);

        var service = CreateService(handler);
        await RunSyncAsync(service);

        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(agentDir, "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(agentDir, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.True(state.Skills.ContainsKey("stale-agent"));
        Assert.Equal("0.9.0", state.Skills["code-reviewer"].Version);
    }

    [Fact]
    public async Task SyncOnce_invalid_native_artifact_keeps_existing_managed_subagents_and_skips_prune()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        var oldContent = AgentMarkdown("code-reviewer", "Old reviewer", "Old body.");
        File.WriteAllText(Path.Combine(agentDir, "code-reviewer.md"), oldContent);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["code-reviewer"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent)
                },
                ["stale-agent"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = "stale"
                }
            }
        });

        var invalidContent = """
            ---
            name: code-reviewer
            tools: [file_read]
            ---

            Missing a description, so the runtime loader would reject this artifact.
            """;
        var digest = SkillSyncHelpers.ComputeSha256(invalidContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", invalidContent, digest);

        var service = CreateService(handler);
        await RunSyncAsync(service);

        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(agentDir, "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(agentDir, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.True(state.Skills.ContainsKey("stale-agent"));
        Assert.Equal("0.9.0", state.Skills["code-reviewer"].Version);
    }

    [Fact]
    public async Task SyncOnce_invalid_utf8_native_artifact_keeps_existing_managed_subagents_and_skips_prune()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        var oldContent = AgentMarkdown("code-reviewer", "Old reviewer", "Old body.");
        File.WriteAllText(Path.Combine(agentDir, "code-reviewer.md"), oldContent);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["code-reviewer"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent)
                },
                ["stale-agent"] = new SyncedSkillState
                {
                    Version = "0.9.0",
                    Sha256 = "stale"
                }
            }
        });

        var validPrefix = Encoding.UTF8.GetBytes("""
            ---
            name: code-reviewer
            description: Managed reviewer
            ---

            Review code carefully.
            """);
        var invalidContent = validPrefix.Concat([byte.MaxValue]).ToArray();
        var digest = SkillSyncHelpers.ComputeSha256(invalidContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", invalidContent, digest);

        var service = CreateService(handler);
        await RunSyncAsync(service);

        Assert.Equal(oldContent, File.ReadAllText(Path.Combine(agentDir, "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(agentDir, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.True(state.Skills.ContainsKey("stale-agent"));
        Assert.Equal("0.9.0", state.Skills["code-reviewer"].Version);
    }

    [Fact]
    public async Task SyncOnce_successful_sidecar_prunes_only_removed_managed_subagents()
    {
        var agentDir = _paths.ServerFeedAgentDirectory("team");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "stale-agent.md"), AgentMarkdown("stale-agent", "Stale", "Stale body."));
        File.WriteAllText(Path.Combine(_paths.AgentsDirectory, "stale-agent.md"),
            AgentMarkdown("stale-agent", "Local stale", "Local must survive."));

        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["stale-agent"] = new SyncedSkillState { Version = "0.9.0", Sha256 = "stale" }
            }
        });

        var agentContent = AgentMarkdown("code-reviewer", "Managed reviewer", "Review code carefully.");
        var digest = SkillSyncHelpers.ComputeSha256(agentContent);

        var handler = new FakeHttpMessageHandler();
        AddEmptyRfcIndex(handler);
        AddNativeSubAgentResponses(handler, "code-reviewer", "1.0.0", agentContent, digest);

        var service = CreateService(handler);
        await RunSyncAsync(service);

        Assert.False(File.Exists(Path.Combine(agentDir, "stale-agent.md")));
        Assert.True(File.Exists(Path.Combine(_paths.AgentsDirectory, "stale-agent.md")));

        var state = ReadAgentSyncState();
        Assert.Equal(["code-reviewer"], state.Skills.Keys.OrderBy(k => k));
    }

    private ServerFeedSkillSyncService CreateService(ISkillContentScanner? scanner = null)
        => new(
            new SkillFeedsConfig(),
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            TimeProvider.System,
            scanner ?? new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            []);

    private static async Task RunSyncAsync(ServerFeedSkillSyncService service)
    {
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.SyncAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private ServerFeedSkillSyncService CreateService(FakeHttpMessageHandler handler)
    {
        var feedsConfig = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds = [new SkillFeedSource { Name = "team", Url = BaseUrl, TimeoutSeconds = 30 }]
        };

        return new ServerFeedSkillSyncService(
            feedsConfig,
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            TimeProvider.System,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            [],
            feed =>
            {
                var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(feed.Url.TrimEnd('/') + "/")
                };
                if (feed.ApiKey is { Value: { Length: > 0 } apiKey })
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                return new SkillServerClient(client);
            },
            TimeSpan.Zero);
    }

    private ServerFeedSkillSyncService CreateControlledService(
        ControlledFeedHandler handler,
        int syncIntervalMinutes,
        bool enabled = true)
        => CreateControlledService(
            handler,
            syncIntervalMinutes,
            enabled,
            NullLogger<ServerFeedSkillSyncService>.Instance);

    private ServerFeedSkillSyncService CreateControlledService(
        ControlledFeedHandler handler,
        int syncIntervalMinutes,
        ILogger<ServerFeedSkillSyncService> logger)
        => CreateControlledService(handler, syncIntervalMinutes, true, logger);

    private ServerFeedSkillSyncService CreateControlledService(
        ControlledFeedHandler handler,
        int syncIntervalMinutes,
        bool enabled,
        ILogger<ServerFeedSkillSyncService> logger)
    {
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = syncIntervalMinutes,
            Feeds = enabled
                ? [new SkillFeedSource { Name = "team", Url = BaseUrl, TimeoutSeconds = 30 }]
                : [],
        };
        return new ServerFeedSkillSyncService(
            feeds,
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            new FakeTimeProvider(),
            new NoOpSkillContentScanner(),
            logger,
            [],
            feed => new SkillServerClient(new HttpClient(handler)
            {
                BaseAddress = new Uri(feed.Url),
            }),
            TimeSpan.Zero);
    }

    private ServerFeedSkillSyncService CreateService(
        SkillFeedsConfig feeds,
        HttpMessageHandler handler,
        ISkillContentScanner scanner,
        TimeProvider timeProvider) => new(
            feeds,
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            timeProvider,
            scanner,
            NullLogger<ServerFeedSkillSyncService>.Instance,
            [],
            feed => new SkillServerClient(new HttpClient(handler)
            {
                BaseAddress = new Uri(feed.Url),
            }),
            TimeSpan.Zero);

    private SkillSyncState ReadAgentSyncState()
    {
        var json = File.ReadAllText(_paths.ServerFeedAgentSyncStatePath("team"));
        return JsonSerializer.Deserialize<SkillSyncState>(json)!;
    }

    private static void AddEmptyRfcIndex(FakeHttpMessageHandler handler)
    {
        handler.AddStringResponse(
            BaseUrl + ".well-known/agent-skills/index.json",
            """
            {
              "skills": []
            }
            """,
            "application/json");
    }

    private sealed class CapturingLogger : ILogger<ServerFeedSkillSyncService>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            Entries.Enqueue(new LogEntry(formatter(state, exception), fields));
        }

        public sealed record LogEntry(string Message, IReadOnlyDictionary<string, object?> Fields);
    }

    private sealed class ControlledFeedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _releaseIndex = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _holdIndex;
        private int _indexRequestCount;

        public ControlledFeedHandler(bool holdIndex) => _holdIndex = holdIndex;

        public TaskCompletionSource FirstIndexRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondIndexRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int IndexRequestCount => Volatile.Read(ref _indexRequestCount);

        public void ReleaseIndex() => _releaseIndex.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/agent-skills/index.json", StringComparison.Ordinal))
            {
                var requestCount = Interlocked.Increment(ref _indexRequestCount);
                FirstIndexRequest.TrySetResult();
                if (requestCount == 2)
                    SecondIndexRequest.TrySetResult();
                if (_holdIndex)
                    await _releaseIndex.Task.WaitAsync(cancellationToken);

                return JsonResponse("{\"skills\":[]}");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/subagents/v1/index.json", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        internal static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class MultiFeedHandler : HttpMessageHandler
    {
        private readonly bool _failEveryFeed;

        public MultiFeedHandler(bool failEveryFeed = false) => _failEveryFeed = failEveryFeed;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "failed.test" || _failEveryFeed)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            if (request.RequestUri.AbsolutePath.EndsWith("/.well-known/agent-skills/index.json", StringComparison.Ordinal))
                return Task.FromResult(ControlledFeedHandler.JsonResponse("{\"skills\":[]}"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TimerSignalTimeProvider : FakeTimeProvider
    {
        public TaskCompletionSource<TimerCreation> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TimerCreation> PeriodicTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            var creation = new TimerCreation(GetUtcNow(), dueTime, period);
            TimerCreated.TrySetResult(creation);
            if (period != Timeout.InfiniteTimeSpan)
                PeriodicTimerCreated.TrySetResult(creation);
            return timer;
        }

        public sealed record TimerCreation(DateTimeOffset CreatedAt, TimeSpan DueTime, TimeSpan Period);
    }

    private ServerFeedSkillSyncService CreateLifecycleService(
        LifetimeScanner scanner,
        TimeProvider timeProvider,
        int intervalMinutes)
        => CreateLifecycleService(
            scanner,
            timeProvider,
            intervalMinutes,
            NullLogger<ServerFeedSkillSyncService>.Instance);

    private ServerFeedSkillSyncService CreateLifecycleService(
        LifetimeScanner scanner,
        TimeProvider timeProvider,
        int intervalMinutes,
        ILogger<ServerFeedSkillSyncService> logger)
    {
        var feeds = new SkillFeedsConfig
        {
            SyncIntervalMinutes = intervalMinutes,
            Feeds = [new SkillFeedSource { Name = "team", Url = BaseUrl, TimeoutSeconds = 30 }],
        };
        var handler = new LifecycleFeedHandler();
        return new ServerFeedSkillSyncService(
            feeds,
            _paths,
            _skillRegistry,
            _skillIndexPublisher,
            timeProvider,
            scanner,
            logger,
            [],
            feed => new SkillServerClient(new HttpClient(handler)
            {
                BaseAddress = new Uri(feed.Url),
            }),
            TimeSpan.Zero);
    }

    private sealed class LifetimeScanner(int blockOnScan, bool observeCancellation) : ISkillContentScanner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _scanCount;

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LifetimeToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<SkillScanResult> ScanAsync(string skillName, string content, CancellationToken cancellationToken)
        {
            LifetimeToken = cancellationToken;
            if (Interlocked.Increment(ref _scanCount) != blockOnScan)
                return SkillScanResult.Allow();

            Blocked.TrySetResult();
            try
            {
                // Delayed acknowledgment models a dependency that cannot stop immediately.
                if (observeCancellation)
                    await _release.Task.WaitAsync(cancellationToken);
                else
                    await _release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return SkillScanResult.Allow();
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
    }

    private sealed class LifecycleFeedHandler : HttpMessageHandler
    {
        private int _revision;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/agent-skills/index.json", StringComparison.Ordinal))
            {
                _revision++;
                var index = new
                {
                    skills = new[]
                    {
                        new
                        {
                            name = "lifecycle-probe",
                            type = "skill",
                            description = "Lifecycle probe.",
                            url = BaseUrl + "probe/SKILL.md",
                            digest = "sha256:" + SkillSyncHelpers.ComputeSha256(SkillContent()),
                            version = _revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        },
                    },
                };
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(index));
            }

            if (request.RequestUri.AbsolutePath == "/probe/SKILL.md")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SkillContent(), Encoding.UTF8, "text/markdown"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string SkillContent() => $"---\nname: lifecycle-probe\ndescription: Lifecycle probe.\n---\nRevision {_revision}.";
    }

    private static void AddNativeSubAgentResponses(
        FakeHttpMessageHandler handler,
        string name,
        string version,
        string artifactContent,
        string expectedDigest)
        => AddNativeSubAgentResponses(handler, name, version, Encoding.UTF8.GetBytes(artifactContent), expectedDigest);

    private static void AddNativeSubAgentResponses(
        FakeHttpMessageHandler handler,
        string name,
        string version,
        byte[] artifactContent,
        string expectedDigest)
    {
        // Use absolute hrefs so the client resolves direct native index traversal correctly.
        handler.AddStringResponse(
            BaseUrl + "subagents/v1/index.json",
            """
            {
              "kind": "subagent-collection-index",
              "links": { "self": { "href": "/subagents/v1/index.json" } },
              "pages": [
                { "range": "a-z", "href": "/subagents/v1/pages/a-z.json" }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(
            BaseUrl + "subagents/v1/pages/a-z.json",
            $$"""
            {
              "kind": "subagent-collection-page",
              "range": "a-z",
              "links": { "self": { "href": "/subagents/v1/pages/a-z.json" } },
              "items": [
                {
                  "name": "{{name}}",
                  "latestVersion": "{{version}}",
                  "versionRange": { "min": "{{version}}", "max": "{{version}}", "count": 1 },
                  "href": "/subagents/v1/{{name}}/index.json"
                }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(
            BaseUrl + $"subagents/v1/{name}/index.json",
            $$"""
            {
              "kind": "subagent-identity-index",
              "name": "{{name}}",
              "latestVersion": "{{version}}",
              "links": { "self": { "href": "/subagents/v1/{{name}}/index.json" } },
              "versions": [
                {
                  "version": "{{version}}",
                  "publishedAt": "2026-06-30T00:00:00Z",
                  "digest": "sha256:{{expectedDigest}}",
                  "href": "/subagents/v1/{{name}}/versions/{{version}}.json"
                }
              ]
            }
            """,
            "application/json");
        handler.AddStringResponse(
            BaseUrl + $"subagents/v1/{name}/versions/{version}.json",
            $$"""
            {
              "kind": "subagent-version-detail",
              "name": "{{name}}",
              "version": "{{version}}",
              "type": "agent-md",
              "description": "Test sub-agent",
              "url": "{{BaseUrl}}subagents/{{name}}/{{version}}/agent.md",
              "digest": "sha256:{{expectedDigest}}",
              "links": { "self": { "href": "/subagents/v1/{{name}}/versions/{{version}}.json" } }
            }
            """,
            "application/json");
        handler.AddByteResponse(
            BaseUrl + $"subagents/{name}/{version}/agent.md",
            artifactContent,
            "text/markdown");
    }

    private static string AgentMarkdown(string name, string description, string body)
        => $"""
           ---
           name: {name}
           description: {description}
           tools: [file_read]
           ---

           {body}
           """;

    private static byte[] BuildArchive(params (string Path, byte[] Content, int UnixMode)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content, unixMode) in entries)
            {
                var entry = archive.CreateEntry(path);
                entry.ExternalAttributes = unixMode << 16;
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }
}
