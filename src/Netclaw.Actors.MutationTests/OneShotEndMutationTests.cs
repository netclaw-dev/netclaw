// -----------------------------------------------------------------------
// <copyright file="OneShotEndMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
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

/// <summary>
/// Focused mutation target for the end of a one-shot reminder (change <c>oneshot-reminder-autodelete</c>,
/// design D4 and D5). Two comparisons decide whether Netclaw ends or keeps a definition:
/// the tombstone match (same id, a one-shot, equal fire time to the millisecond) and the check that the
/// settled occurrence is the current definition (equal fire time to the millisecond). A mutant of either
/// comparison deletes a definition that must stay, or keeps one that must go.
/// Each case asserts on the files the manager leaves behind, so no case depends on a timer.
/// </summary>
public sealed class OneShotEndMutationTests : IAsyncLifetime
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-oneshot-end-mutations", Guid.NewGuid().ToString("N")));
    private readonly HeldPipeline _pipeline = new();
    private readonly ConcurrentQueue<OperationalAlert> _alerts = new();
    private readonly Channel<Guid> _dispatches = Channel.CreateUnbounded<Guid>();
    private readonly ReminderId _id = new("end-probe");
    private IHost _host = null!;
    private IActorRef _manager = null!;
    private ReminderDefinitionStore _definitions = null!;
    private ReminderHistoryStore _history = null!;

    public async Task InitializeAsync()
    {
        _definitions = new ReminderDefinitionStore(_paths);
        _history = new ReminderHistoryStore(_paths);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAkka($"oneshot-end-mutations-{Guid.NewGuid():N}", akka => akka
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
                    new AlertSink(_alerts), NullReminderChannelNotifier.Instance)));
            }));
        _host = builder.Build();
        await _host.StartAsync();
        // The first reply proves that PreStart queued the reconcile. The second waits behind it.
        await ReconcileAsync();
        await ReconcileAsync();
    }

    // ── D4: the tombstone match ──

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(3_600_000, false)]
    public async Task A_tombstone_matches_only_when_the_fire_times_are_equal_to_the_millisecond(
        long deltaMs, bool matches)
    {
        var definition = OneShot(FireInOneHour());
        _definitions.Save(definition);
        WriteTombstone("Completed", definition.Schedule.FireAtMs!.Value + deltaMs);
        var tombstone = File.ReadAllText(TombstonePath());

        await ReconcileAsync();

        Assert.Equal(matches, _definitions.Get(_id) is null);
        Assert.Equal(!matches, _definitions.Get(_id)?.Enabled ?? false);
        Assert.Equal(matches, File.Exists(TombstonePath()));
        if (matches)
            Assert.Equal(tombstone, File.ReadAllText(TombstonePath()));
    }

    [Theory]
    [InlineData(ReminderScheduleType.Interval)]
    [InlineData(ReminderScheduleType.Cron)]
    public async Task A_tombstone_never_matches_a_recurring_definition(ReminderScheduleType type)
    {
        var fireAt = FireInOneHour();
        var definition = OneShot(fireAt) with
        {
            Schedule = type == ReminderScheduleType.Interval
                ? new ReminderSchedule { Type = type, FireAt = fireAt, Interval = TimeSpan.FromHours(1) }
                : new ReminderSchedule { Type = type, CronExpression = "0 0 1 1 *" }
        };
        _definitions.Save(definition);
        WriteTombstone("Completed", fireAt.ToUnixTimeMilliseconds());

        await ReconcileAsync();

        Assert.True(_definitions.Get(_id)?.Enabled);
        Assert.False(File.Exists(TombstonePath()), "A tombstone that cannot match is stale and must go.");
    }

    // ── D5: the settled occurrence is the current definition ──

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(600_000, false)]
    public async Task A_settlement_ends_the_definition_only_when_its_fire_time_equals_the_due_time_to_the_millisecond(
        long deltaMs, bool ends)
    {
        // The due time carries half a millisecond more than the definition: the comparison must be in milliseconds.
        var dueMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var due = DateTimeOffset.FromUnixTimeMilliseconds(dueMs).AddTicks(TimeSpan.TicksPerMillisecond / 2);
        _definitions.Save(OneShot(DateTimeOffset.FromUnixTimeMilliseconds(dueMs + deltaMs)));
        var executionId = await DispatchAsync(due);
        var before = _definitions.Get(_id);

        await CompleteAsync(executionId, success: true);

        Assert.Equal(ends, _definitions.Get(_id) is null);
        Assert.Equal(ends, File.Exists(TombstonePath()));
        if (!ends)
            Assert.Equal(before, _definitions.Get(_id));
        Assert.Single(await _history.ReadAsync(_id, 10));
    }

    [Fact]
    public async Task A_settlement_with_no_definition_does_not_replace_the_tombstone()
    {
        var due = DateTimeOffset.UtcNow;
        _definitions.Save(OneShot(due));
        var executionId = await DispatchAsync(due);
        _definitions.Delete(_id);
        WriteTombstone("Cancelled", due.ToUnixTimeMilliseconds());
        var tombstone = File.ReadAllText(TombstonePath());

        await CompleteAsync(executionId, success: true);

        Assert.Equal(tombstone, File.ReadAllText(TombstonePath()));
        Assert.Single(await _history.ReadAsync(_id, 10));
    }

    // ── Helpers ──

    private static DateTimeOffset FireInOneHour()
        => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());

    private ReminderDefinition OneShot(DateTimeOffset fireAt)
    {
        var now = DateTimeOffset.UtcNow;
        return new ReminderDefinition
        {
            Id = _id,
            Title = "End probe",
            Instructions = "Record the outcome.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = fireAt },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "mutation-test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private string TombstonePath()
        => Path.Combine(_paths.RemindersDirectory, "ended", $"{Uri.EscapeDataString(_id.Value)}.json");

    private void WriteTombstone(string outcome, long fireAtMs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TombstonePath())!);
        File.WriteAllText(TombstonePath(), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = _id.Value,
            ["title"] = "End probe",
            ["audience"] = "Team",
            ["fireAtMs"] = fireAtMs,
            ["outcome"] = outcome,
            ["endedAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["lastError"] = null
        }));
    }

    private async Task<Guid> DispatchAsync(DateTimeOffset due)
    {
        _manager.Tell(new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(_id.Value), due, ReminderDeadline.Infinite, new ReminderPayload { Id = _id }));
        return await _dispatches.Reader.ReadAsync().AsTask().WaitAsync(ReplyTimeout);
    }

    private async Task CompleteAsync(Guid executionId, bool success)
    {
        var accepted = await _manager.Ask<ReminderExecutionAccepted>(new ReminderExecutionCompleted(
            executionId, _id, success,
            new HistoryRecord(DateTimeOffset.UtcNow, success, 10, $"reminder/{executionId:N}", success ? null : "probe failure"),
            success ? null : "probe failure"), ReplyTimeout);
        Assert.Equal(executionId, accepted.ExecutionId);
    }

    private Task<ReminderManagerActor.ReconcileCompleted> ReconcileAsync() =>
        _manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, ReplyTimeout);

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

    private sealed class AlertSink(ConcurrentQueue<OperationalAlert> items) : IOperationalNotificationSink
    {
        public void Emit(OperationalAlert alert) => items.Enqueue(alert);
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
