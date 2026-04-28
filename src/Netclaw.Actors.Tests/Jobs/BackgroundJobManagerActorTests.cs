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
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

public class BackgroundJobManagerActorTests : TestKit
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-jobs-{Guid.NewGuid():N}");
    private BackgroundJobDefinitionStore _store = null!;

    public BackgroundJobManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var paths = new NetclawPaths(_tempDir);
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

    private IActorRef GetManager() => ActorRegistry.For(Sys).Get<BackgroundJobManagerActorKey>();

    private StartBackgroundJob MakeStartCommand(string command = "echo hello") => new()
    {
        Command = command,
        SessionId = new SessionId("test/thread"),
        Rationale = "test run",
        Audience = TrustAudience.Personal,
        Boundary = SecurityPolicyDefaults.PersonalBoundary,
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
    public async Task StartupReconciliation_MarksOrphanedJobsAsLost()
    {
        // The manager was already created in ConfigureAkka and reconciled on PreStart.
        // Pre-populate a "running" job after startup, then create a second manager
        // to verify reconciliation.
        var orphanDef = new BackgroundJobDefinition
        {
            Id = "orphan-123",
            Command = "sleep 999",
            SessionId = "test/thread",
            Rationale = "orphaned test",
            Status = BackgroundJobStatus.Running,
            StartedAtMs = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.PersonalBoundary,
            OriginChannelType = ChannelType.Tui
        };
        _store.Save(orphanDef);

        // Create a second manager — its PreStart reconciliation should mark the orphan as Lost
        Sys.ActorOf(
            Props.Create(() => new BackgroundJobManagerActor(_store, TimeProvider.System)),
            "reconcile-test-manager");

        await AwaitAssertAsync(() =>
        {
            var reconciled = _store.Get(new BackgroundJobId("orphan-123"));
            Assert.NotNull(reconciled);
            Assert.Equal(BackgroundJobStatus.Lost, reconciled!.Status);
            Assert.NotNull(reconciled.CompletedAtMs);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }
}
