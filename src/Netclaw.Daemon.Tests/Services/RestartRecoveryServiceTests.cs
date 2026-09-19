// -----------------------------------------------------------------------
// <copyright file="RestartRecoveryServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Services;

public sealed class RestartRecoveryServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly ActorSystem _system;
    private readonly NetclawPaths _paths;

    public RestartRecoveryServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _system = ActorSystem.Create($"restart-recovery-tests-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task StartAsync_warms_manifest_sessions_and_marks_catalog_active()
    {
        var warmedSessions = new ConcurrentQueue<WarmSession>();
        var actor = _system.ActorOf(Props.Create(() => new WarmSessionActor(warmedSessions)));
        var manifestStore = new RestartManifestStore(_paths);
        var catalog = new SessionCatalogService(
            _paths,
            TimeProvider.System,
            new TestSessionStorageResolver(_paths),
            NullLogger<SessionCatalogService>.Instance);
        var sessionId = new SessionId("slack/C123/1710000000.000001");

        catalog.OnSessionActivated(sessionId, Netclaw.Actors.Channels.ChannelType.Slack);
        catalog.OnSessionDeactivated(sessionId);

        await manifestStore.WriteAsync(new RestartManifest
        {
            Reason = "config-reload",
            RequestedAt = TimeProvider.System.GetUtcNow(),
            SessionIds = [sessionId.Value],
            TimedOutSessionIds = [sessionId.Value]
        }, CancellationToken.None);

        var sut = new RestartRecoveryService(
            manifestStore,
            new StubRequiredActor(actor),
            catalog,
            [],
            TimeProvider.System,
            NullLogger<RestartRecoveryService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(warmedSessions.TryDequeue(out var warmed));
        Assert.Equal(sessionId, warmed!.SessionId);
        Assert.Contains("last durable checkpoint", warmed.RestartNotice, StringComparison.Ordinal);
        Assert.Null(await manifestStore.ReadAsync(CancellationToken.None));

        var entry = Assert.Single(catalog.ListRecent());
        Assert.Equal("active", entry.Status);
    }

    [Fact]
    public async Task Recovery_binds_output_before_it_claims_the_original_turn()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-18T12:00:00Z"));
        var sessionId = new SessionId("C123/1710000000.000001");
        var candidate = new RestartResumeCandidate(
            sessionId, ["input-original"], ["input-queued"],
            time.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds());
        var binder = new RecordingBinder();
        var actor = _system.ActorOf(Props.Create(() => new ResumeSessionActor(candidate, binder)));
        var store = new RestartManifestStore(_paths);
        await store.WriteAsync(new RestartManifest
        {
            GenerationId = Guid.NewGuid(),
            Reason = "normal-stop",
            RequestedAt = time.GetUtcNow(),
            SessionIds = [sessionId.Value],
            ResumeCandidates = [candidate]
        }, CancellationToken.None);

        var sut = new RestartRecoveryService(
            store,
            new StubRequiredActor(actor),
            CreateCatalog(time),
            [binder],
            time,
            NullLogger<RestartRecoveryService>.Instance);
        await sut.StartAsync(CancellationToken.None);
        await sut.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, binder.BindCount);
        Assert.Equal(1, binder.ResumeCount);
        Assert.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_output_route_keeps_candidate_until_its_fixed_deadline()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-18T12:00:00Z"));
        var sessionId = new SessionId("C123/1710000000.000002");
        var candidate = new RestartResumeCandidate(sessionId, ["input-original"], [],
            time.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds());
        var binder = new RecordingBinder { Ready = false };
        var actor = _system.ActorOf(Props.Create(() => new ResumeSessionActor(candidate, binder)));
        var store = new RestartManifestStore(_paths);
        await store.WriteAsync(new RestartManifest
        {
            GenerationId = Guid.NewGuid(),
            Reason = "normal-stop",
            RequestedAt = time.GetUtcNow(),
            SessionIds = [sessionId.Value],
            ResumeCandidates = [candidate]
        }, CancellationToken.None);

        var sut = new RestartRecoveryService(
            store, new StubRequiredActor(actor), CreateCatalog(time), [binder],
            time, NullLogger<RestartRecoveryService>.Instance);
        await sut.StartAsync(CancellationToken.None);
        await binder.FirstBind.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(await store.ReadAsync(CancellationToken.None));
        Assert.Equal(0, binder.ResumeCount);

        time.Advance(TimeSpan.FromMinutes(11));
        await sut.RecoveryTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, binder.ResumeCount);
        Assert.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Old_recovery_cannot_remove_a_new_restart_manifest()
    {
        var store = new RestartManifestStore(_paths);
        var oldGeneration = Guid.NewGuid();
        var newGeneration = Guid.NewGuid();
        var candidate = new RestartResumeCandidate(
            new SessionId("C123/1710000000.000003"), ["input-original"], [], 123456789);
        await store.WriteAsync(new RestartManifest
        {
            GenerationId = oldGeneration,
            Reason = "first-stop",
            RequestedAt = DateTimeOffset.Parse("2026-09-18T12:00:00Z"),
            SessionIds = [candidate.SessionId.Value],
            ResumeCandidates = [candidate]
        }, CancellationToken.None);
        await store.WriteAsync(new RestartManifest
        {
            GenerationId = newGeneration,
            Reason = "second-stop",
            RequestedAt = DateTimeOffset.Parse("2026-09-18T12:01:00Z"),
            SessionIds = [candidate.SessionId.Value],
            ResumeCandidates = [candidate]
        }, CancellationToken.None);

        Assert.False(await store.TryUpdateCandidatesAsync(oldGeneration, [], CancellationToken.None));
        var remaining = Assert.IsType<RestartManifest>(await store.ReadAsync(CancellationToken.None));
        Assert.Equal(newGeneration, remaining.GenerationId);
        Assert.Single(remaining.ResumeCandidates);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(_paths.RestartManifestPath));
    }

    private SessionCatalogService CreateCatalog(TimeProvider time) => new(
        _paths, time, new TestSessionStorageResolver(_paths),
        NullLogger<SessionCatalogService>.Instance);

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        SqliteTestPools.Clear(_paths);
        _dir.Dispose();
    }

    private sealed class StubRequiredActor : IRequiredActor<SessionManagerActorKey>
    {
        public StubRequiredActor(IActorRef actorRef)
        {
            ActorRef = actorRef;
        }

        public IActorRef ActorRef { get; }

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActorRef);
    }

    private sealed class WarmSessionActor : ReceiveActor
    {
        public WarmSessionActor(ConcurrentQueue<WarmSession> warmedSessions)
        {
            Receive<WarmSession>(msg =>
            {
                warmedSessions.Enqueue(msg);
                Sender.Tell(CommandAck.For(msg.SessionId));
            });
        }
    }

    private sealed class ResumeSessionActor : ReceiveActor
    {
        public ResumeSessionActor(RestartResumeCandidate candidate, RecordingBinder binder)
        {
            Receive<WarmSession>(message => Sender.Tell(CommandAck.For(message.SessionId)));
            Receive<GetRestartResumeRoute>(message => Sender.Tell(new RestartResumeRouteResult(
                message.SessionId,
                [new TurnContextRecord
                {
                    SessionId = message.SessionId,
                    TurnId = "original-turn",
                    ChannelType = "slack",
                    Boundary = TrustBoundary.Personal,
                    RequesterPrincipal = PrincipalClassification.Operator,
                    RequesterSenderId = new SenderId("U123")
                }], null, null)));
            Receive<ResumeInterruptedSession>(message =>
            {
                if (message.Candidate.SessionId != candidate.SessionId || binder.BindCount == 0)
                    throw new InvalidOperationException("The route must be ready before resume.");
                binder.RecordResume();
                Sender.Tell(CommandAck.For(message.SessionId));
            });
        }
    }

    private sealed class RecordingBinder : IChannel, IRestartOutputBinder
    {
        private int _bindCount;
        private int _resumeCount;

        public bool Ready { get; init; } = true;

        public int BindCount => Volatile.Read(ref _bindCount);

        public int ResumeCount => Volatile.Read(ref _resumeCount);

        public TaskCompletionSource FirstBind { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelType ChannelType => ChannelType.Slack;

        public string DisplayName => "Slack test";

        public Task<RestartOutputBindingResult> BindForRestartAsync(
            RestartResumeRouteResult route, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bindCount);
            FirstBind.TrySetResult();
            return Task.FromResult(new RestartOutputBindingResult(Ready, Ready ? null : "Route offline"));
        }

        public void RecordResume() => Interlocked.Increment(ref _resumeCount);

        public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Healthy));

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
