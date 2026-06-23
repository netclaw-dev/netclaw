// -----------------------------------------------------------------------
// <copyright file="BackgroundJobManagerActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

[Collection(BackgroundJobProcessCollection.Name)]
public class BackgroundJobManagerActorTests : TestKit
{
    private readonly DisposableTempDir _dir = new();
    private BackgroundJobDefinitionStore _store = null!;

    public BackgroundJobManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _store = new BackgroundJobDefinitionStore(paths);

        builder.StartActors((system, registry, _) =>
        {
            var manager = system.ActorOf(
                Props.Create(() => new BackgroundJobManagerActor(_store, TimeProvider.System)),
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

    private StartBackgroundJob MakeStartCommand(string command = "echo hello") => new()
    {
        Command = command,
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
                MakeStartCommand($"sleep {i + 60}"),
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
                MakeStartCommand($"sleep {i + 60}"),
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
            MakeStartCommand("sleep 300") with { SessionId = sessionA },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var jobA2 = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand("sleep 300") with { SessionId = sessionA },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var jobB = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand("sleep 300") with { SessionId = sessionB },
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
            MakeStartCommand("sleep 300") with { SessionId = sessionId },
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

        // Pre-populate a "running" job with streamed output on disk, simulating
        // a job that was alive when the daemon went down.
        var orphanId = new BackgroundJobId("lost-notify-1");
        _store.Save(new BackgroundJobDefinition
        {
            Id = orphanId,
            Command = "jekyll serve",
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
            Props.Create(() => new BackgroundJobManagerActor(_store, TimeProvider.System)),
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
        Assert.Equal($"bg-job:{orphanId.Value}", delivery.Source.BackgroundJobId);
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
            Props.Create(() => new BackgroundJobManagerActor(_store, TimeProvider.System)),
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
            Props.Create(() => new BackgroundJobManagerActor(store, TimeProvider.System, sink)),
            "legacy-job-alert-manager");

        // Readiness barrier: startup alert emission runs before this reply.
        await legacyManager.Ask<BackgroundJobManagerHealthResponse>(
            GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains(sink.Alerts, alert =>
            alert.Category == AlertType.BackgroundJobSchemaDropped
            && alert.Summary.Contains(jobId, StringComparison.Ordinal));
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
}
