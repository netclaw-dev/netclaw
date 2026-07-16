// -----------------------------------------------------------------------
// <copyright file="DaemonRestartCoordinatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Services;

public sealed class DaemonRestartCoordinatorTests : IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);

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
        var time = new FakeTimeProvider();
        var (coordinator, drain) = CreateCoordinator(
            ["slack/C123.1", "slack/C123.2"],
            timeProvider: time);

        var restart = coordinator.RequestConfigRestartAsync(CancellationToken.None);
        await drain.AllRequestsObserved;
        drain.AcknowledgeAll();
        await drain.AllAcknowledgementsSent;
        await restart;

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
        var time = new FakeTimeProvider();
        var logger = new DrainAcknowledgementLogger();
        var (coordinator, drain) = CreateCoordinator(
            ["slack/C123.1", "slack/C123.2"],
            timedOutSessionIds: ["slack/C123.2"],
            timeProvider: time,
            logger: logger);

        var restart = coordinator.RequestConfigRestartAsync(CancellationToken.None);
        await drain.AllRequestsObserved;
        drain.AcknowledgeAll();
        await logger.Acknowledged;
        time.Advance(DrainTimeout);
        await restart;

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
    public async Task RequestConfigRestartAsync_propagates_caller_cancellation_and_reopens_ingress()
    {
        var time = new FakeTimeProvider();
        var (coordinator, drain) = CreateCoordinator(
            ["slack/C123.1"],
            timedOutSessionIds: ["slack/C123.1"],
            timeProvider: time);
        using var callerCts = new CancellationTokenSource();

        var restart = coordinator.RequestConfigRestartAsync(callerCts.Token);
        await drain.AllRequestsObserved;
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restart);
        Assert.False(_restartSignal.RestartRequested);
        Assert.False(_appLifetime.StopRequested);
        Assert.Null(_ingressGate.ClosedReason);
    }

    [Fact]
    public async Task RequestConfigRestartAsync_reopens_ingress_when_coordination_fails()
    {
        var time = new FakeTimeProvider();
        var (coordinator, _) = CreateCoordinator(
            [],
            throwOnEnumeration: true,
            timeProvider: time);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RequestConfigRestartAsync(CancellationToken.None));

        Assert.False(_restartSignal.RestartRequested);
        Assert.False(_appLifetime.StopRequested);
        Assert.Null(_ingressGate.ClosedReason);
    }

    [Fact]
    public async Task SessionDrainHelper_reports_deadline_timeouts()
    {
        var time = new FakeTimeProvider();
        var activeIds = new[] { "slack/drain-timeout" };
        var timedOut = new[] { "slack/drain-timeout" };
        var drain = new DrainControl(activeIds, timedOut);
        var sessionManager = _system.ActorOf(Props.Create(() => new StubSessionManagerActor(
            activeIds,
            drain,
            throwOnEnumeration: false)));
        using var deadlineCts = new CancellationTokenSource(DrainTimeout, time);

        var operation = SessionDrainHelper.DrainAsync(
            sessionManager,
            "integration-test",
            NullLogger<DaemonRestartCoordinator>.Instance,
            deadlineCts.Token,
            CancellationToken.None);
        await drain.AllRequestsObserved;
        time.Advance(DrainTimeout);

        var result = await operation;

        Assert.Single(result.AllSessionIds);
        Assert.Empty(result.DrainedSessionIds);
        Assert.Single(result.TimedOutSessionIds);
        Assert.Equal("slack/drain-timeout", result.TimedOutSessionIds[0].Value);

        var context = result.ToNotificationContext();
        Assert.Equal("timeout", context["drainOutcome"]);
        Assert.Equal("1", context["activeSessions"]);
        Assert.Equal("0", context["drainedSessions"]);
        Assert.Equal("1", context["timedOutSessions"]);
    }

    [Fact]
    public async Task SessionDrainHelper_propagates_caller_cancellation()
    {
        var activeIds = new[] { "slack/drain-cancelled" };
        var drain = new DrainControl(activeIds, activeIds);
        var sessionManager = _system.ActorOf(Props.Create(() => new StubSessionManagerActor(
            activeIds,
            drain,
            throwOnEnumeration: false)));
        using var callerCts = new CancellationTokenSource();

        var operation = SessionDrainHelper.DrainAsync(
            sessionManager,
            "integration-test",
            NullLogger<DaemonRestartCoordinator>.Instance,
            callerCts.Token,
            callerCts.Token);
        await drain.AllRequestsObserved;
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task SessionDrainHelper_daemon_stop_bound_times_out_instead_of_hanging_when_a_session_never_acks()
    {
        // Mirrors the daemon-stop CoordinatedShutdown drain task wired in Program.cs
        // (netclaw-dev/netclaw#1664): a session whose in-flight turn is parked on interactive
        // tool approval never acks PrepareForDaemonRestart. Previously this call passed
        // CancellationToken.None for the operation token and hung until Akka's own 200s
        // before-service-unbind phase timeout abandoned the task. The bounded CTS below —
        // sized from DaemonConfig.BoundedDrainTimeout (GracefulShutdownBudget minus
        // DrainSafetyMargin) and driven by TimeProvider exactly as Program.cs constructs it —
        // must make the drain complete with a timed-out result well before that.
        var time = new FakeTimeProvider();
        var activeIds = new[] { "slack/approval-parked" };
        var drain = new DrainControl(activeIds, activeIds); // never acknowledged
        var sessionManager = _system.ActorOf(Props.Create(() => new StubSessionManagerActor(
            activeIds,
            drain,
            throwOnEnumeration: false)));
        using var deadlineCts = new CancellationTokenSource(DaemonConfig.BoundedDrainTimeout, time);

        var operation = SessionDrainHelper.DrainAsync(
            sessionManager,
            "daemon-stop",
            NullLogger<DaemonRestartCoordinator>.Instance,
            deadlineCts.Token,
            CancellationToken.None);
        await drain.AllRequestsObserved;
        time.Advance(DaemonConfig.BoundedDrainTimeout);

        var result = await operation;

        Assert.Single(result.AllSessionIds);
        Assert.Empty(result.DrainedSessionIds);
        Assert.Equal("slack/approval-parked", Assert.Single(result.TimedOutSessionIds).Value);
    }

    public async ValueTask DisposeAsync()
    {
        await _system.Terminate();
        _dir.Dispose();
    }

    private (DaemonRestartCoordinator Coordinator, DrainControl Drain) CreateCoordinator(
        IReadOnlyList<string> activeSessionIds,
        IReadOnlyList<string>? timedOutSessionIds = null,
        bool throwOnEnumeration = false,
        FakeTimeProvider? timeProvider = null,
        ILogger<DaemonRestartCoordinator>? logger = null)
    {
        var timedOut = timedOutSessionIds ?? Array.Empty<string>();
        var drain = new DrainControl(activeSessionIds, timedOut);
        var sessionManager = _system.ActorOf(Props.Create(() => new StubSessionManagerActor(
            activeSessionIds,
            drain,
            throwOnEnumeration)));
        var time = timeProvider ?? new FakeTimeProvider();
        var notifier = new DaemonLifecycleNotifier(
            _sink,
            time,
            NullLogger<DaemonLifecycleNotifier>.Instance);

        var coordinator = new DaemonRestartCoordinator(
            _ingressGate,
            new RestartManifestStore(_paths),
            new StubRequiredActor(sessionManager),
            _restartSignal,
            _appLifetime,
            notifier,
            time,
            logger ?? NullLogger<DaemonRestartCoordinator>.Instance,
            DrainTimeout);

        return (coordinator, drain);
    }

    private sealed class StubRequiredActor(IActorRef actorRef) : IRequiredActor<SessionManagerActorKey>
    {
        public IActorRef ActorRef { get; } = actorRef;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActorRef);
    }

    private sealed class StubSessionManagerActor : ReceiveActor
    {
        public StubSessionManagerActor(
            IReadOnlyList<string> activeSessionIds,
            DrainControl drain,
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

            Receive<PrepareForDaemonRestart>(msg => drain.Observe(msg, Sender));
        }
    }

    private sealed class DrainControl
    {
        private readonly HashSet<string> _timedOutSessionIds;
        private readonly ConcurrentDictionary<string, IActorRef> _pendingRequests = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _allRequestsObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allAcknowledgementsSent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedRequestCount;
        private readonly int _expectedAcknowledgementCount;
        private int _requestCount;
        private int _acknowledgementCount;

        public DrainControl(
            IReadOnlyList<string> activeSessionIds,
            IReadOnlyList<string> timedOutSessionIds)
        {
            _timedOutSessionIds = timedOutSessionIds.ToHashSet(StringComparer.Ordinal);
            _expectedRequestCount = activeSessionIds.Count;
            _expectedAcknowledgementCount = activeSessionIds.Count - _timedOutSessionIds.Count;

            if (_expectedRequestCount == 0)
                _allRequestsObserved.SetResult();
            if (_expectedAcknowledgementCount == 0)
                _allAcknowledgementsSent.SetResult();
        }

        public Task AllRequestsObserved => _allRequestsObserved.Task;

        public Task AllAcknowledgementsSent => _allAcknowledgementsSent.Task;

        public void Observe(PrepareForDaemonRestart request, IActorRef replyTo)
        {
            if (!_pendingRequests.TryAdd(request.SessionId.Value, replyTo))
                throw new InvalidOperationException($"Duplicate drain request for {request.SessionId.Value}.");

            if (Interlocked.Increment(ref _requestCount) == _expectedRequestCount)
                _allRequestsObserved.TrySetResult();
        }

        public void AcknowledgeAll()
        {
            foreach (var (sessionId, replyTo) in _pendingRequests)
            {
                if (_timedOutSessionIds.Contains(sessionId))
                    continue;

                replyTo.Tell(CommandAck.For(new SessionId(sessionId)));
                if (Interlocked.Increment(ref _acknowledgementCount) == _expectedAcknowledgementCount)
                    _allAcknowledgementsSent.TrySetResult();
            }
        }
    }

    private sealed class DrainAcknowledgementLogger : ILogger<DaemonRestartCoordinator>
    {
        private readonly TaskCompletionSource _acknowledged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Acknowledged => _acknowledged.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId == SessionDrainHelper.SessionDrainAcknowledgedEvent)
                _acknowledged.TrySetResult();
        }
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
