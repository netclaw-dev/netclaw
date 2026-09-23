// -----------------------------------------------------------------------
// <copyright file="ReminderExecutionMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Reminders;
using Akka.Streams;
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
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-reminder-mutations", Guid.NewGuid().ToString("N")));
    private readonly HeldPipeline _pipeline = new();
    private readonly AlertSink _alerts = new();
    private readonly Channel<Guid> _dispatches = Channel.CreateUnbounded<Guid>();
    private readonly ReminderId _id = new("ownership-probe");
    private readonly DateTimeOffset _now = TimeProvider.System.GetUtcNow();
    private IHost _host = null!;
    private IActorRef _manager = null!;
    private IActorRef _barrier = null!;
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

    private async Task<Guid> DispatchAsync()
    {
        _manager.Tell(new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(_id.Value), _now.AddSeconds(++_occurrence), ReminderDeadline.Infinite,
            new ReminderPayload { Id = _id }));
        var executionId = await _dispatches.Reader.ReadAsync().AsTask().WaitAsync(ReplyTimeout);
        Assert.Equal(1, (await HealthAsync()).ActiveExecutions);
        return executionId;
    }

    private async Task CompleteAsync(Guid executionId, bool success)
    {
        var accepted = await _manager.Ask<ReminderExecutionAccepted>(new ReminderExecutionCompleted(
            executionId, _id, success,
            new HistoryRecord(_now, success, 10, $"reminder/{executionId:N}", success ? null : "probe failure"),
            success ? null : "probe failure"), ReplyTimeout);
        Assert.Equal(executionId, accepted.ExecutionId);
    }

    private Task<ReminderHealthResponse> HealthAsync() =>
        _manager.Ask<ReminderHealthResponse>(GetReminderHealthQuery.Instance, ReplyTimeout);

    private Task<ReminderHealthResponse> TerminateAsync(Guid executionId) =>
        _barrier.Ask<ReminderHealthResponse>(new ReminderExecutionTerminated(executionId, _id), ReplyTimeout);

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
}
