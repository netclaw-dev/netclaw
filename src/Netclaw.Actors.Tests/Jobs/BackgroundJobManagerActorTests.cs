// -----------------------------------------------------------------------
// <copyright file="BackgroundJobManagerActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Jobs;

[Collection(BackgroundJobProcessCollection.Name)]
public class BackgroundJobManagerActorTests : TestKit
{
    private readonly DisposableTempDir _dir = new();
    private BackgroundJobDefinitionStore _store = null!;
    private string? _rejectedOutputDirectory;

    public BackgroundJobManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _store = new BackgroundJobDefinitionStore(
            paths,
            NullLogger<BackgroundJobDefinitionStore>.Instance,
            DeleteOutputDirectory);

        builder.StartActors((system, registry, _) =>
        {
            var manager = system.ActorOf(
                Props.Create(() => new BackgroundJobManagerActor(
                    _store,
                    TimeProvider.System,
                    TestShellEnvironment.Current)),
                "background-job-manager");
            registry.Register<BackgroundJobManagerActorKey>(manager);
        });
    }

    protected override async Task AfterAllAsync()
    {
        _dir.Dispose();
        await base.AfterAllAsync();
    }

    private IActorRef GetManager() => ActorRegistry.For(Sys).Get<BackgroundJobManagerActorKey>();

    private async Task<IActorRef> GetReadyManagerAsync()
    {
        var manager = GetManager();
        await manager.Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        return manager;
    }

    private void DeleteOutputDirectory(string path, bool recursive)
    {
        if (string.Equals(path, Volatile.Read(ref _rejectedOutputDirectory), StringComparison.Ordinal))
            throw new IOException("simulated output cleanup failure");

        Directory.Delete(path, recursive);
    }

    private async Task<BackgroundJobManagerHealthResponse> RunTerminalSweepAsync(IActorRef manager)
    {
        // Both messages use the same sender, so the health response is a strict
        // mailbox barrier after the sweep.
        manager.Tell(BackgroundJobManagerActor.SweepTerminalJobs.Instance, TestActor);
        manager.Tell(GetBackgroundJobManagerHealth.Instance, TestActor);
        return await ExpectMsgAsync<BackgroundJobManagerHealthResponse>(
            TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
    }

    private BackgroundJobDefinition MakeTerminalDefinition(string jobId, BackgroundJobStatus status, long completedAtMs) => new()
    {
        Id = new BackgroundJobId(jobId),
        Command = "echo hello",
        ManagedTemporaryDirectory = Path.Combine(_dir.Path, "managed-temp"),
        ManagedTemporaryAuthorityRoot = _dir.Path,
        SessionId = new SessionId("test/thread"),
        Rationale = "test run",
        Status = status,
        StartedAtMs = completedAtMs - 60_000,
        CompletedAtMs = completedAtMs,
        TimeoutSeconds = 60,
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        OriginChannelType = ChannelType.Tui
    };

    private StartBackgroundJob MakeStartCommand(string command = "echo hello") => new()
    {
        Command = command,
        ManagedTemporaryDirectory = Path.Combine(Path.GetTempPath(), "netclaw-tests", "managed-temp"),
        ManagedTemporaryStorageRoot = Path.Combine(Path.GetTempPath(), "netclaw-tests"),
        SessionId = new SessionId("test/thread"),
        Rationale = "test run",
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        OriginChannelType = ChannelType.Tui,
        TimeoutSeconds = 60
    };

    [Fact]
    public async Task ConcurrencyLimit_QueuesOverflowJobs()
    {
        var manager = GetManager();
        var jobIds = new List<BackgroundJobId>();

        for (var i = 0; i < BackgroundJobManagerActor.MaxConcurrentJobs + 2; i++)
        {
            var started = await manager.Ask<BackgroundJobStarted>(
                MakeStartCommand(TestShellEnvironment.DelayCommand(i + 60)),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            jobIds.Add(started.JobId);
        }

        Assert.Equal(BackgroundJobManagerActor.MaxConcurrentJobs + 2, jobIds.Count);
        Assert.Equal(jobIds.Count, jobIds.Distinct().Count());
    }

    [Fact]
    public async Task Completion_DispatchesQueuedJob()
    {
        var manager = GetManager();
        var jobIds = new List<BackgroundJobId>();

        for (var i = 0; i < BackgroundJobManagerActor.MaxConcurrentJobs + 1; i++)
        {
            var started = await manager.Ask<BackgroundJobStarted>(
                MakeStartCommand(TestShellEnvironment.DelayCommand(i + 60)),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            jobIds.Add(started.JobId);
        }

        // Simulate completion of first job — manager should dispatch the queued job
        manager.Tell(new BackgroundJobCompleted
        {
            JobId = jobIds[0],
            Status = BackgroundJobStatus.Completed,
            ExitCode = 0,
            Duration = TimeSpan.FromSeconds(1)
        });

        // The queued job (last one) should now be running — verify its definition updated to Running
        await AwaitAssertAsync(() =>
        {
            var def = _store.Get(jobIds[^1]);
            Assert.NotNull(def);
            Assert.Equal(BackgroundJobStatus.Running, def!.Status);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task KillJobsForSession_ReapsOwnedJobs_LeavesOtherSessionsAlone()
    {
        var manager = GetManager();
        var sessionA = new SessionId("reap/session-a");
        var sessionB = new SessionId("reap/session-b");

        var jobA1 = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand(TestShellEnvironment.LongRunningCommand) with { SessionId = sessionA },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var jobA2 = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand(TestShellEnvironment.LongRunningCommand) with { SessionId = sessionA },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var jobB = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand(TestShellEnvironment.LongRunningCommand) with { SessionId = sessionB },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Guard against environmental spawn failure (fork pressure under the
        // parallel suite): all three processes must actually be running before
        // the reap, or the count assertions below report a misleading cause.
        await AwaitAssertAsync(() =>
        {
            Assert.Equal(BackgroundJobStatus.Running, _store.Get(jobA1.JobId)!.Status);
            Assert.Equal(BackgroundJobStatus.Running, _store.Get(jobA2.JobId)!.Status);
            Assert.Equal(BackgroundJobStatus.Running, _store.Get(jobB.JobId)!.Status);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var ack = await manager.Ask<SessionJobsReaped>(
            new KillJobsForSession(sessionA),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(sessionA, ack.SessionId);
        Assert.Equal(2, ack.ReapedCount);

        Assert.Equal(BackgroundJobStatus.Reaped, _store.Get(jobA1.JobId)!.Status);
        Assert.Equal(BackgroundJobStatus.Reaped, _store.Get(jobA2.JobId)!.Status);
        Assert.Equal(BackgroundJobStatus.Running, _store.Get(jobB.JobId)!.Status);

        // The reaped status survives the child's Cancelled completion report.
        await AwaitAssertAsync(() =>
        {
            var def = _store.Get(jobA1.JobId);
            Assert.NotNull(def!.CompletedAtMs);
            Assert.Equal(BackgroundJobStatus.Reaped, def.Status);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReapedJob_ProducesNoCompletionDelivery()
    {
        var manager = GetManager();
        var sessionId = new SessionId("reap/no-delivery");

        // Stand in for the TUI/SignalR gateway so a delivery, if (wrongly)
        // produced, would be observable.
        var gatewayProbe = CreateTestProbe("gateway");
        ActorRegistry.For(Sys).Register<SignalRGatewayActorKey>(gatewayProbe.Ref);

        var started = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand(TestShellEnvironment.LongRunningCommand) with { SessionId = sessionId },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await manager.Ask<SessionJobsReaped>(
            new KillJobsForSession(sessionId),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Wait for the child's Cancelled report to round-trip through
        // HandleCompleted (CompletedAtMs set), then assert no delivery follows.
        await AwaitAssertAsync(() =>
        {
            Assert.NotNull(_store.Get(started.JobId)!.CompletedAtMs);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        await gatewayProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(500),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task KillJobsForSession_WithNoOwnedJobs_AcksZero()
    {
        var manager = GetManager();

        var ack = await manager.Ask<SessionJobsReaped>(
            new KillJobsForSession(new SessionId("reap/empty-session")),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, ack.ReapedCount);
    }

    [Fact]
    public async Task StartupReconciliation_DeliversLostNotificationToOwningSession()
    {
        var sessionId = new SessionId("lost/notify-session");
        var gatewayProbe = CreateTestProbe("lost-gateway");
        ActorRegistry.For(Sys).Register<SignalRGatewayActorKey>(gatewayProbe.Ref, overwrite: true);

        // The manager created in ConfigureAkka runs its PreStart reconciliation
        // on the dispatcher, not necessarily before this test body. If it
        // reconciles after the orphan is persisted but before its output.log
        // exists, it delivers a "lost" notification with no output path
        // (NotifyLostJob swallows the missing-file error and nulls
        // OutputFilePath). PreStart completes before any user message is
        // dispatched, so a successful health reply proves this manager
        // reconciled while the jobs directory was still empty — it can then
        // never deliver for this orphan. Only the fresh manager below
        // (created after the log write) reconciles the orphan, so the
        // notification always carries the full output path.
        await GetManager().Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Pre-populate a "running" job with streamed output on disk, simulating
        // a job that was alive when the daemon went down.
        var orphanId = new BackgroundJobId("lost-notify-1");
        _store.Save(new BackgroundJobDefinition
        {
            Id = orphanId,
            Command = "jekyll serve",
            ManagedTemporaryDirectory = Path.Combine(_dir.Path, "managed-temp"),
            ManagedTemporaryAuthorityRoot = _dir.Path,
            SessionId = sessionId,
            Rationale = "dev server",
            Status = BackgroundJobStatus.Running,
            StartedAtMs = TimeProvider.System.GetUtcNow().AddMinutes(-5).ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            OriginChannelType = ChannelType.Tui
        });
        var logPath = _store.GetOutputLogPath(orphanId);
        await File.WriteAllTextAsync(
            logPath, "Server running on http://127.0.0.1:4000/\n",
            TestContext.Current.CancellationToken);

        // A fresh manager's PreStart reconciliation marks the orphan Lost and
        // must notify the owning session through the gateway.
        var manager = Sys.ActorOf(
            Props.Create(() => new BackgroundJobManagerActor(
                _store,
                TimeProvider.System,
                TestShellEnvironment.Current)),
            "lost-notify-manager");

        // Readiness barrier: reconciliation runs before this reply.
        await manager.Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var delivery = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, delivery.SessionId);
        Assert.Contains("was lost", delivery.Content);
        Assert.Contains("lost-notify-1", delivery.Content);
        Assert.Contains(logPath, delivery.Content);
        Assert.Contains("Server running on", delivery.Content);
        Assert.Equal(TrustAudience.Personal, delivery.Source.Audience);
        Assert.Equal(new BackgroundJobId($"bg-job:{orphanId.Value}"), delivery.Source.BackgroundJobId);
    }

    [Fact]
    public async Task StartupReconciliation_MarksOrphanedJobsAsLost()
    {
        // The manager was already created in ConfigureAkka and reconciled on PreStart.
        // Pre-populate a "running" job after startup, then create a second manager
        // to verify reconciliation.
        var orphanDef = new BackgroundJobDefinition
        {
            Id = new BackgroundJobId("orphan-123"),
            Command = "sleep 999",
            ManagedTemporaryDirectory = Path.Combine(_dir.Path, "managed-temp"),
            ManagedTemporaryAuthorityRoot = _dir.Path,
            SessionId = new Netclaw.Actors.Protocol.SessionId("test/thread"),
            Rationale = "orphaned test",
            Status = BackgroundJobStatus.Running,
            StartedAtMs = TimeProvider.System.GetUtcNow().AddMinutes(-30).ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            OriginChannelType = ChannelType.Tui
        };
        _store.Save(orphanDef);

        // Create a second manager — its PreStart reconciliation should mark the orphan as Lost
        var manager = Sys.ActorOf(
            Props.Create(() => new BackgroundJobManagerActor(
                _store,
                TimeProvider.System,
                TestShellEnvironment.Current)),
            "reconcile-test-manager");

        // Readiness barrier: reconciliation runs before this reply.
        await manager.Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var reconciled = _store.Get(new BackgroundJobId("orphan-123"));
        Assert.NotNull(reconciled);
        Assert.Equal(BackgroundJobStatus.Lost, reconciled!.Status);
        Assert.NotNull(reconciled.CompletedAtMs);
    }

    [Fact]
    public async Task StartupReconciliation_EmitsAlert_ForLegacyJobMissingTrustFields()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        const string jobId = "legacy-job-alert";
        var filePath = Path.Combine(_dir.Path, "jobs", $"{Uri.EscapeDataString(jobId)}.json");
        File.WriteAllText(filePath, $$"""
            {
              "id": "{{jobId}}",
              "command": "echo hello",
              "sessionId": "test/thread",
              "rationale": "legacy job",
              "status": "Pending",
              "timeoutSeconds": 60,
              "startedAtMs": 0
            }
            """);

        var store = new BackgroundJobDefinitionStore(paths);
        var sink = new RecordingNotificationSink();

        var legacyManager = Sys.ActorOf(
            Props.Create(() => new BackgroundJobManagerActor(
                store,
                TimeProvider.System,
                TestShellEnvironment.Current,
                sink)),
            "legacy-job-alert-manager");

        // Readiness barrier: startup alert emission runs before this reply.
        await legacyManager.Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains(sink.Alerts, alert =>
            alert.Category == AlertType.BackgroundJobSchemaDropped
            && alert.Summary.Contains(jobId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupReconciliation_MarksOldJobWithoutManagedTemporaryFieldsAsLost()
    {
        await GetReadyManagerAsync();
        var sessionId = new SessionId("legacy-temp/session");
        var gatewayProbe = CreateTestProbe("legacy-temp-gateway");
        ActorRegistry.For(Sys).Register<SignalRGatewayActorKey>(gatewayProbe.Ref, overwrite: true);

        const string jobId = "legacy-temp-orphan";
        var filePath = Path.Combine(_dir.Path, "jobs", $"{jobId}.json");
        File.WriteAllText(filePath,
            $$"""
              {
                "id": "{{jobId}}",
                "command": "dotnet test",
                "sessionId": "{{sessionId.Value}}",
                "rationale": "legacy persisted job",
                "status": "Running",
                "timeoutSeconds": 600,
                "startedAtMs": 1,
                "audience": "Personal",
                "boundary": "Personal",
                "originChannelType": "Tui"
              }
              """);

        var manager = Sys.ActorOf(
            Props.Create(() => new BackgroundJobManagerActor(
                _store,
                TimeProvider.System,
                TestShellEnvironment.Current)),
            "legacy-temp-reconcile-manager");

        await manager.Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        var reconciled = _store.Get(new BackgroundJobId(jobId));
        Assert.NotNull(reconciled);
        Assert.Equal(BackgroundJobStatus.Lost, reconciled!.Status);
        Assert.Null(reconciled.ManagedTemporaryDirectory);
        Assert.Null(reconciled.ManagedTemporaryAuthorityRoot);

        var delivery = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, delivery.SessionId);
        Assert.Contains("was lost", delivery.Content, StringComparison.Ordinal);
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        private readonly object _sync = new();
        private readonly List<OperationalAlert> _alerts = [];

        public IReadOnlyList<OperationalAlert> Alerts
        {
            get
            {
                lock (_sync)
                    return _alerts.ToArray();
            }
        }

        public void Emit(OperationalAlert alert)
        {
            lock (_sync)
                _alerts.Add(alert);
        }
    }

    [Fact]
    public async Task TerminalSweep_DeletesJobPastRetentionWindow()
    {
        var manager = await GetReadyManagerAsync();
        var pastWindowMs = TimeProvider.System.GetUtcNow()
            .Subtract(BackgroundJobManagerActor.TerminalJobRetentionWindow)
            .Subtract(TimeSpan.FromMinutes(1))
            .ToUnixTimeMilliseconds();

        _store.Save(MakeTerminalDefinition("sweep-past", BackgroundJobStatus.Completed, pastWindowMs));

        await RunTerminalSweepAsync(manager);

        Assert.Null(_store.Get(new BackgroundJobId("sweep-past")));
    }

    [Fact]
    public async Task TerminalSweep_KeepsJobWithinRetentionWindow()
    {
        var manager = await GetReadyManagerAsync();
        var recentMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        _store.Save(MakeTerminalDefinition("sweep-recent", BackgroundJobStatus.Completed, recentMs));

        await RunTerminalSweepAsync(manager);

        Assert.NotNull(_store.Get(new BackgroundJobId("sweep-recent")));
    }

    [Fact]
    public async Task TerminalSweep_DoesNotTouchNonTerminalJobs()
    {
        var manager = await GetReadyManagerAsync();
        var pastWindowMs = TimeProvider.System.GetUtcNow()
            .Subtract(BackgroundJobManagerActor.TerminalJobRetentionWindow)
            .Subtract(TimeSpan.FromMinutes(1))
            .ToUnixTimeMilliseconds();

        // A Running job with a stale CompletedAtMs must never be swept — the
        // status guard is independent of the timestamp.
        _store.Save(MakeTerminalDefinition("sweep-running", BackgroundJobStatus.Running, pastWindowMs));
        _store.Save(MakeTerminalDefinition("sweep-pending", BackgroundJobStatus.Pending, pastWindowMs));

        await RunTerminalSweepAsync(manager);

        Assert.NotNull(_store.Get(new BackgroundJobId("sweep-running")));
        Assert.NotNull(_store.Get(new BackgroundJobId("sweep-pending")));
    }

    [Fact]
    public async Task TerminalSweep_DeletesOutputLogWithDefinition()
    {
        var manager = await GetReadyManagerAsync();
        var pastWindowMs = TimeProvider.System.GetUtcNow()
            .Subtract(BackgroundJobManagerActor.TerminalJobRetentionWindow)
            .Subtract(TimeSpan.FromMinutes(1))
            .ToUnixTimeMilliseconds();
        var jobId = new BackgroundJobId("sweep-log");

        var outputLogPath = _store.GetOutputLogPath(jobId);
        File.WriteAllText(outputLogPath, "some output");

        _store.Save(MakeTerminalDefinition("sweep-log", BackgroundJobStatus.Failed, pastWindowMs));

        await RunTerminalSweepAsync(manager);

        Assert.Null(_store.Get(jobId));
        Assert.False(File.Exists(outputLogPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(outputLogPath)));
    }

    [Fact]
    public async Task TerminalSweep_CleanupFailureDoesNotRestartManagerAndLaterSweepRetries()
    {
        var manager = await GetReadyManagerAsync();
        var pastWindowMs = TimeProvider.System.GetUtcNow()
            .Subtract(BackgroundJobManagerActor.TerminalJobRetentionWindow)
            .Subtract(TimeSpan.FromMinutes(1))
            .ToUnixTimeMilliseconds();
        var blocked = MakeTerminalDefinition("sweep-blocked", BackgroundJobStatus.Failed, pastWindowMs);
        var removable = MakeTerminalDefinition("sweep-removable", BackgroundJobStatus.Completed, pastWindowMs);
        _store.Save(blocked);
        _store.Save(removable);

        var blockedOutputPath = _store.GetOutputLogPath(blocked.Id);
        var blockedOutputDirectory = Path.GetDirectoryName(blockedOutputPath)!;
        File.WriteAllText(blockedOutputPath, "failed output");
        File.WriteAllText(_store.GetOutputLogPath(removable.Id), "completed output");
        Volatile.Write(ref _rejectedOutputDirectory, blockedOutputDirectory);

        var active = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand("sleep 60"),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        try
        {
            var health = await RunTerminalSweepAsync(manager);

            Assert.Equal(1, health.ActiveJobCount);
            Assert.NotNull(_store.Get(blocked.Id));
            Assert.Null(_store.Get(removable.Id));

            Volatile.Write(ref _rejectedOutputDirectory, null);
            await RunTerminalSweepAsync(manager);

            Assert.Null(_store.Get(blocked.Id));
        }
        finally
        {
            Volatile.Write(ref _rejectedOutputDirectory, null);

            await manager.Ask<BackgroundJobCancelResponse>(
                new CancelBackgroundJob(
                    active.JobId,
                    new SessionId("test/thread"),
                    TrustAudience.Personal,
                    TrustBoundary.Personal),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TerminalSweep_KeepsTerminalJobWithMissingCompletionTime()
    {
        var manager = await GetReadyManagerAsync();
        var pastWindowMs = TimeProvider.System.GetUtcNow()
            .Subtract(BackgroundJobManagerActor.TerminalJobRetentionWindow)
            .Subtract(TimeSpan.FromMinutes(1))
            .ToUnixTimeMilliseconds();

        var def = MakeTerminalDefinition("sweep-null-completed", BackgroundJobStatus.Failed, pastWindowMs);
        def = def with { CompletedAtMs = null };
        _store.Save(def);
        var outputLogPath = _store.GetOutputLogPath(def.Id);
        File.WriteAllText(outputLogPath, "diagnostic output");

        await RunTerminalSweepAsync(manager);

        Assert.NotNull(_store.Get(def.Id));
        Assert.True(File.Exists(outputLogPath));
    }
}
