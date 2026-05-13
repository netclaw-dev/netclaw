// -----------------------------------------------------------------------
// <copyright file="DaemonRestartCoordinatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class DaemonRestartCoordinatorTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly ActorSystem _system;
    private readonly NetclawPaths _paths;
    private readonly SessionIngressGate _ingressGate = new();
    private readonly DaemonRestartSignal _restartSignal = new();
    private readonly FakeApplicationLifetime _appLifetime = new();
    private readonly RecordingSink _sink = new();

    public DaemonRestartCoordinatorTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _system = ActorSystem.Create($"restart-tests-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task RequestConfigRestartAsync_drains_active_sessions_and_requests_stop()
    {
        var coordinator = CreateCoordinator(["slack/C123.1", "slack/C123.2"], restartDrainTimeout: TimeSpan.FromMilliseconds(250));

        await coordinator.RequestConfigRestartAsync(CancellationToken.None);

        Assert.True(_restartSignal.RestartRequested);
        Assert.True(_appLifetime.StopRequested);
        Assert.Equal(SessionIngressGate.RestartInProgressMessage, _ingressGate.ClosedReason);

        var manifest = await new RestartManifestStore(_paths).ReadAsync(CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(["slack/C123.1", "slack/C123.2"], manifest!.SessionIds);
        Assert.Empty(manifest.TimedOutSessionIds);

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal("drained", alert.Context!["drainOutcome"]);
        Assert.Equal("2", alert.Context["activeSessions"]);
    }

    [Fact]
    public async Task RequestConfigRestartAsync_records_timed_out_sessions()
    {
        var coordinator = CreateCoordinator(
            ["slack/C123.1", "slack/C123.2"],
            timedOutSessionIds: ["slack/C123.2"],
            restartDrainTimeout: TimeSpan.FromSeconds(3));

        await coordinator.RequestConfigRestartAsync(CancellationToken.None);

        Assert.True(_restartSignal.RestartRequested);
        Assert.True(_appLifetime.StopRequested);

        var manifest = await new RestartManifestStore(_paths).ReadAsync(CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(["slack/C123.2"], manifest!.TimedOutSessionIds);

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal("timeout", alert.Context!["drainOutcome"]);
        Assert.Equal("1", alert.Context["timedOutSessions"]);
    }

    [Fact]
    public async Task RequestConfigRestartAsync_reopens_ingress_when_coordination_fails()
    {
        // Drain timeout is intentionally wide. The test asserts that the
        // stub's InvalidOperationException propagates and reopens the
        // ingress gate; a tight timeout (100ms) races with the Ask on
        // slow CI runners and yields TaskCanceledException instead,
        // making this test flake. The timeout is irrelevant to the
        // behavior under test.
        var coordinator = CreateCoordinator([], throwOnEnumeration: true, restartDrainTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestConfigRestartAsync(CancellationToken.None));

        Assert.False(_restartSignal.RestartRequested);
        Assert.False(_appLifetime.StopRequested);
        Assert.Null(_ingressGate.ClosedReason);
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        _dir.Dispose();
    }

    private DaemonRestartCoordinator CreateCoordinator(
        IReadOnlyList<string> activeSessionIds,
        IReadOnlyList<string>? timedOutSessionIds = null,
        bool throwOnEnumeration = false,
        TimeSpan? restartDrainTimeout = null)
    {
        var sessionManager = _system.ActorOf(Props.Create(() => new StubSessionManagerActor(
            activeSessionIds,
            timedOutSessionIds ?? Array.Empty<string>(),
            throwOnEnumeration)));
        var notifier = new DaemonLifecycleNotifier(_sink, TimeProvider.System, NullLogger<DaemonLifecycleNotifier>.Instance);

        return new DaemonRestartCoordinator(
            _ingressGate,
            new RestartManifestStore(_paths),
            new StubRequiredActor(sessionManager),
            _restartSignal,
            _appLifetime,
            notifier,
            TimeProvider.System,
            NullLogger<DaemonRestartCoordinator>.Instance,
            restartDrainTimeout);
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

    private sealed class StubSessionManagerActor : ReceiveActor
    {
        public StubSessionManagerActor(
            IReadOnlyList<string> activeSessionIds,
            IReadOnlyList<string> timedOutSessionIds,
            bool throwOnEnumeration)
        {
            Receive<GetActiveEntityIds>(_ =>
            {
                if (throwOnEnumeration)
                {
                    Sender.Tell(new Status.Failure(new InvalidOperationException("enumeration failed")));
                    return;
                }

                Sender.Tell(new ActiveEntityIds(activeSessionIds));
            });

            Receive<PrepareForDaemonRestart>(msg =>
            {
                if (timedOutSessionIds.Contains(msg.SessionId.Value, StringComparer.Ordinal))
                    return;

                Sender.Tell(CommandAck.For(msg.SessionId));
            });
        }
    }

    [Fact]
    public async Task SessionDrainHelper_queries_manager_drains_sessions_and_reports_timeouts()
    {
        // One session acks normally, the other times out
        var activeIds = new[] { "slack/drain-ok", "slack/drain-timeout" };
        var timedOut = new[] { "slack/drain-timeout" };
        var sessionManager = _system.ActorOf(Props.Create(() => new StubSessionManagerActor(
            activeIds, timedOut, false)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var result = await SessionDrainHelper.DrainAsync(
            sessionManager,
            "integration-test",
            NullLogger<DaemonRestartCoordinator>.Instance,
            cts.Token);

        // All sessions were discovered
        Assert.Equal(2, result.AllSessionIds.Count);

        // One drained, one timed out
        Assert.Single(result.DrainedSessionIds);
        Assert.Equal("slack/drain-ok", result.DrainedSessionIds[0].Value);
        Assert.Single(result.TimedOutSessionIds);
        Assert.Equal("slack/drain-timeout", result.TimedOutSessionIds[0].Value);

        // Notification context reflects the outcome
        var ctx = result.ToNotificationContext();
        Assert.Equal("timeout", ctx["drainOutcome"]);
        Assert.Equal("2", ctx["activeSessions"]);
        Assert.Equal("1", ctx["drainedSessions"]);
        Assert.Equal("1", ctx["timedOutSessions"]);
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }

    private sealed class RecordingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
