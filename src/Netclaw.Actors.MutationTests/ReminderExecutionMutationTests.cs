// -----------------------------------------------------------------------
// <copyright file="ReminderExecutionMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Threading.Channels;
using Akka;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Reminders;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

public sealed class ReminderExecutionMutationTests : IAsyncLifetime
{
    // Two bounds with two jobs. Do not merge them.
    // KillTimeout bounds a wait that a gate mutant can starve: the acceptance reply (the mutant removes
    // it) and a later dispatch (the cleanup mutants keep the guard). Stryker reports a mutant as Killed
    // only when a test fails before its per-mutant time limit, so this bound stays short.
    // ReadyTimeout bounds a wait that every gate mutant still completes: host start, health replies,
    // the termination barrier, the first dispatch of a test, and the local execution. These waits do
    // the cold work of a new process (JIT, a new actor system, the first stream materializer). On a
    // loaded Windows or macOS runner that work took more than 2 s. Correct code always replies, so
    // this bound only stops a real hang.
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-reminder-mutations", Guid.NewGuid().ToString("N")));
    private readonly HeldPipeline _pipeline = new();
    private readonly AlertSink _alerts = new();
    private readonly Channel<Guid> _dispatches = Channel.CreateUnbounded<Guid>();
    private readonly Channel<ReminderExecutionCompleted> _localOutcomes =
        Channel.CreateUnbounded<ReminderExecutionCompleted>();
    private readonly Channel<IActorRef> _localTerminations = Channel.CreateUnbounded<IActorRef>();
    private readonly ReminderId _id = new("ownership-probe");
    private readonly DateTimeOffset _now = TimeProvider.System.GetUtcNow();
    private IHost _host = null!;
    private IActorRef _manager = null!;
    private IActorRef _barrier = null!;
    private IActorRef _localExecutionParent = null!;
    private ReminderDefinitionStore _definitions = null!;
    private ReminderHistoryStore _history = null!;
    private int _occurrence;

    public async Task InitializeAsync()
    {
        _definitions = new ReminderDefinitionStore(_paths);
        _history = new ReminderHistoryStore(_paths);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAkka($"reminder-mutations-{Guid.NewGuid():N}", akka => akka
            .WithLocalReminders(reminders => reminders.WithInMemoryStorage())
            .WithActors((system, _) =>
            {
                var observer = system.ActorOf(Props.Create(() => new DispatchObserver(_dispatches.Writer)));
                system.EventStream.Subscribe(observer, typeof(Info));
                _manager = system.ActorOf(Props.Create(() => new ReminderManagerActor(
                    _pipeline,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false),
                    new SchedulingConfig(), TimeProvider.System, _definitions, _history,
                    _alerts, NullReminderChannelNotifier.Instance)));
                _barrier = system.ActorOf(Props.Create(() => new QueryBarrier(_manager)));
                _localExecutionParent = system.ActorOf(
                    Props.Create(() => new LocalExecutionParent(_localOutcomes.Writer, _localTerminations.Writer)),
                    "local-execution-parent");
            }));
        _host = builder.Build();
        await _host.StartAsync();
        // The first reply proves PreStart queued reconciliation. The second waits behind that message.
        Assert.Equal(0, (await HealthAsync()).ActiveExecutions);
        Assert.Equal(0, (await HealthAsync()).ActiveExecutions);
        _definitions.Save(new ReminderDefinition
        {
            Id = _id,
            Title = "Execution ownership probe",
            Instructions = "Record the outcome.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                FireAt = _now.AddHours(1),
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "mutation-test",
            CreatedAt = _now,
            UpdatedAt = _now,
            ConsecutiveFailures = 2
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Old_completion_cannot_settle_a_new_attempt(bool staleSuccess)
    {
        var oldId = await DispatchAsync();
        await CompleteAsync(oldId, success: true);
        var currentId = await DispatchAsync();
        Assert.NotEqual(oldId, currentId);
        var before = await HistoryAsync();

        await CompleteAsync(oldId, staleSuccess);

        Assert.Equal(1, (await HealthAsync()).ActiveExecutions);
        Assert.Equal(before, await HistoryAsync());
        Assert.Equal(0, Definition().ConsecutiveFailures);
        Assert.Empty(_alerts.Items);

        await CompleteAsync(currentId, success: false);
        Assert.Equal(0, (await HealthAsync()).ActiveExecutions);
        Assert.Equal(1, Definition().ConsecutiveFailures);
        Assert.Equal(2, (await HistoryAsync()).Length);
        await DispatchAsync();
    }

    [Fact]
    public async Task Old_termination_preserves_the_new_owner_but_current_termination_releases_it()
    {
        var oldId = await DispatchAsync();
        await CompleteAsync(oldId, success: true);
        var currentId = await DispatchAsync();
        Assert.NotEqual(oldId, currentId);
        var before = await HistoryAsync();

        var staleHealth = await TerminateAsync(oldId);

        Assert.Equal(1, staleHealth.ActiveExecutions);
        Assert.Equal(before, await HistoryAsync());
        Assert.Equal(0, Definition().ConsecutiveFailures);
        Assert.Empty(_alerts.Items);

        var currentHealth = await TerminateAsync(currentId);
        Assert.Equal(0, currentHealth.ActiveExecutions);
        Assert.Equal(1, Definition().ConsecutiveFailures);
        var history = await HistoryAsync();
        Assert.Equal(2, history.Length);
        var failure = Assert.Single(history, record => !record.Success);
        Assert.Equal("Reminder execution actor terminated unexpectedly.", failure.ErrorMessage);
        await DispatchAsync();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Outcome_releases_its_guard_and_acknowledges_even_when_state_cannot_save(
        bool success, bool failSave)
    {
        var executionId = await DispatchAsync();
        if (failSave)
        {
            // A directory at the atomic-write path causes a real, portable I/O failure.
            Directory.CreateDirectory(Path.Combine(_paths.RemindersDirectory, $"{_id.Value}.json.tmp"));
        }

        await CompleteAsync(executionId, success);

        Assert.Equal(0, (await HealthAsync()).ActiveExecutions);
        Assert.Equal(failSave ? 2 : success ? 0 : 3, Definition().ConsecutiveFailures);
        Assert.Equal(success, Assert.Single(await HistoryAsync()).Success);
        if (failSave)
            Assert.Contains(_alerts.Items, alert => alert.Type == "reminder.settlement.failed");
        await DispatchAsync();
    }

    [Fact]
    public async Task Unrelated_failed_turn_does_not_settle_local_reminder()
    {
        // Arrange
        var dueAt = _now.AddMinutes(1);
        var expectedKey = new ReminderId($"{_id.Value}:{dueAt.ToUnixTimeMilliseconds()}");
        var definition = Definition() with
        {
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "origin-session",
                OriginChannelType = ChannelType.Reminder
            }
        };
        var pipeline = new TerminalSequencePipeline(
        [
            new TurnCompleted
            {
                SessionId = new SessionId("origin-session"),
                TurnNumber = new TurnNumber(1),
                Outcome = TurnOutcome.Failed,
                SourceReminderId = new ReminderId("different-reminder:1"),
                OpenChildRunIds = []
            },
            new TurnCompleted
            {
                SessionId = new SessionId("origin-session"),
                TurnNumber = new TurnNumber(2),
                Outcome = TurnOutcome.Completed,
                SourceReminderId = expectedKey,
                OpenChildRunIds = []
            }
        ]);
        var executionId = Guid.NewGuid();
        var envelope = new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(_id.Value), dueAt, ReminderDeadline.Infinite,
            new ReminderPayload { Id = _id });

        // Act
        var started = await _localExecutionParent.Ask<LocalExecutionStarted>(
            new StartLocalExecution(executionId, definition, pipeline, envelope), ReadyTimeout);
        var outcome = await _localOutcomes.Reader.ReadAsync().AsTask().WaitAsync(ReadyTimeout);
        var input = await pipeline.InputCaptured.WaitAsync(ReadyTimeout);
        var terminated = await _localTerminations.Reader.ReadAsync().AsTask().WaitAsync(ReadyTimeout);

        // Assert
        Assert.Equal(executionId, started.ExecutionId);
        Assert.Equal(started.Execution, terminated);
        Assert.Equal(executionId, outcome.ExecutionId);
        Assert.Equal(_id, outcome.Id);
        Assert.True(outcome.Success);
        Assert.Null(outcome.ErrorMessage);
        Assert.Equal(expectedKey, input.ReminderId);
        Assert.NotNull(input.AckTarget);
    }

    private async Task<Guid> DispatchAsync()
    {
        // No gate mutant can stop the first dispatch of a test: the tracker is new and the schedule is
        // an interval. A later dispatch follows a release, and the cleanup mutants block it.
        var dispatchTimeout = _occurrence == 0 ? ReadyTimeout : KillTimeout;
        _manager.Tell(new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(_id.Value), _now.AddSeconds(++_occurrence), ReminderDeadline.Infinite,
            new ReminderPayload { Id = _id }));
        var executionId = await _dispatches.Reader.ReadAsync().AsTask().WaitAsync(dispatchTimeout);
        Assert.Equal(1, (await HealthAsync()).ActiveExecutions);
        return executionId;
    }

    private async Task CompleteAsync(Guid executionId, bool success)
    {
        var accepted = await _manager.Ask<ReminderExecutionAccepted>(new ReminderExecutionCompleted(
            executionId, _id, success,
            new HistoryRecord(_now, success, 10, $"reminder/{executionId:N}", success ? null : "probe failure"),
            success ? null : "probe failure"), KillTimeout);
        Assert.Equal(executionId, accepted.ExecutionId);
    }

    private Task<ReminderHealthResponse> HealthAsync() =>
        _manager.Ask<ReminderHealthResponse>(GetReminderHealthQuery.Instance, ReadyTimeout);

    private Task<ReminderHealthResponse> TerminateAsync(Guid executionId) =>
        _barrier.Ask<ReminderHealthResponse>(new ReminderExecutionTerminated(executionId, _id), ReadyTimeout);

    private ReminderDefinition Definition() => Assert.IsType<ReminderDefinition>(_definitions.Get(_id));

    private async Task<HistoryRecord[]> HistoryAsync() =>
        (await _history.ReadAsync(_id, ReminderHistoryStore.MaxRecords)).ToArray();

    public async Task DisposeAsync()
    {
        try
        {
            if (_host is not null)
                await _host.StopAsync();
        }
        finally
        {
            _pipeline.Release();
            _host?.Dispose();
        }
        Directory.Delete(_paths.BasePath, recursive: true);
    }

    private sealed class DispatchObserver : ReceiveActor
    {
        public DispatchObserver(ChannelWriter<Guid> writer)
        {
            Receive<Info>(info =>
            {
                const string prefix = "ReminderExecution Dispatched: execution_id=";
                if (info.LogClass == typeof(ReminderExecutionActor)
                    && info.Message is string message
                    && message.StartsWith(prefix, StringComparison.Ordinal))
                    writer.TryWrite(Guid.Parse(message.AsSpan(prefix.Length, 36)));
            });
        }
    }

    private sealed class QueryBarrier : ReceiveActor
    {
        public QueryBarrier(IActorRef manager)
        {
            Receive<ReminderExecutionTerminated>(terminated =>
            {
                var replyTo = Sender;
                manager.Tell(terminated, Self);
                manager.Tell(GetReminderHealthQuery.Instance, Self);
                BecomeStacked(() => Receive<ReminderHealthResponse>(health =>
                {
                    replyTo.Tell(health);
                    UnbecomeStacked();
                }));
            });
        }
    }

    private sealed class AlertSink : IOperationalNotificationSink
    {
        public ConcurrentQueue<OperationalAlert> Items { get; } = new();
        public void Emit(OperationalAlert alert) => Items.Enqueue(alert);
    }

    private sealed class HeldPipeline : ISessionPipeline
    {
        private readonly TaskCompletionSource<MaterializedSession> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId, SessionPipelineOptions options,
            IMaterializer? materializer = null, CancellationToken cancellationToken = default) =>
            _release.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetCanceled();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            throw new InvalidOperationException("The held pipeline must not receive feedback.");

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            throw new InvalidOperationException("The held pipeline must not receive feedback.");
    }

    private sealed record StartLocalExecution(
        Guid ExecutionId,
        ReminderDefinition Definition,
        ISessionPipeline Pipeline,
        ReminderEnvelope<ReminderPayload> Envelope);

    private sealed record LocalExecutionStarted(Guid ExecutionId, IActorRef Execution);

    private sealed class LocalExecutionParent : ReceiveActor
    {
        public LocalExecutionParent(
            ChannelWriter<ReminderExecutionCompleted> outcomes,
            ChannelWriter<IActorRef> terminations)
        {
            Receive<StartLocalExecution>(start =>
            {
                var execution = Context.ActorOf(
                    ReminderExecutionActor.CreateProps(
                        start.ExecutionId, start.Definition, start.Pipeline, TimeProvider.System, start.Envelope),
                    $"execution-{start.ExecutionId:N}");
                Context.Watch(execution);
                Sender.Tell(new LocalExecutionStarted(start.ExecutionId, execution));
            });
            Receive<ReminderExecutionCompleted>(completed =>
            {
                if (!outcomes.TryWrite(completed))
                    throw new InvalidOperationException("The local outcome channel is closed.");

                Sender.Tell(new ReminderExecutionAccepted(completed.ExecutionId));
            });
            Receive<Terminated>(terminated =>
            {
                if (!terminations.TryWrite(terminated.ActorRef))
                    throw new InvalidOperationException("The local termination channel is closed.");
            });
        }
    }

    private sealed class TerminalSequencePipeline(IReadOnlyList<SessionOutput> outputs) : ISessionPipeline
    {
        private readonly TaskCompletionSource _inputAccepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ChannelInput> _inputCaptured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChannelInput> InputCaptured => _inputCaptured.Task;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            var killSwitch = KillSwitches.Shared($"terminal-sequence-{sessionId.Value}");
            var input = Sink.ForEach<ChannelInput>(channelInput =>
            {
                _inputCaptured.TrySetResult(channelInput);
                channelInput.AckTarget?.Tell(CommandAck.For(sessionId), ActorRefs.NoSender);
                _inputAccepted.TrySetResult();
            }).ObservingFault();
            var output = Source.UnfoldAsync<int, SessionOutput>(0, async index =>
                {
                    await _inputAccepted.Task.ConfigureAwait(false);
                    if (index < outputs.Count)
                        return (index + 1, outputs[index]);

                    return default;
                })
                .Concat(Source.Maybe<SessionOutput>().MapMaterializedValue(_ => NotUsed.Instance))
                .Via(killSwitch.Flow<SessionOutput>());

            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}
