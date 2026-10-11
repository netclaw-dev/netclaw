// -----------------------------------------------------------------------
// <copyright file="ReminderManagerProductionRetryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Akka.TestKit;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Hosting;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using TestKit = Akka.Hosting.TestKit.TestKit;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Runs the reminder manager against the real Akka.Reminders scheduler with the
/// production settings: the 70-minute acknowledgement timeout and the library
/// default retry backoff, which starts at 60 seconds. The actor system uses
/// <see cref="TestScheduler"/>, so the scheduler clock and its timers move only
/// when a test advances them. Each run fails, as it does when the model
/// endpoint is not available.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class ReminderManagerProductionRetryTests : TestKit, IAsyncDisposable
{
    private static readonly TimeSpan FirstFireDelay = TimeSpan.FromSeconds(10);

    private static readonly ReminderAudienceAuthorizationContext OperatorAuthorization =
        new(TrustAudience.Personal, "Operator/test");

    private readonly TestSessionTempDirectory _tempDir =
        TestSessionTempDirectory.Create(prefix: "netclaw-reminder-retry-tests-", createDirectoryTree: true);
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly TestShardRegionResolver _sharedResolver = new();
    private readonly ReminderManagerActorTests.TestNotificationSink _notificationSink = new();
    private readonly ReminderManagerActorTests.FailingReminderSessionPipeline _sessionPipeline =
        new("model endpoint is not available");
    private ReminderDefinitionStore _definitionStore = null!;

    public ReminderManagerProductionRetryTests(ITestOutputHelper output) : base(output: output) { }

    // TestKit stops the actor system only after AfterAllAsync returns. Delete the directory
    // after TestKit has disposed, and not in AfterAllAsync.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _tempDir.DisposeAsync();
        }
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon(
            "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"",
            HoconAddMode.Prepend);

        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSerializationVerification();

        _definitionStore = new ReminderDefinitionStore(_tempDir.Paths);
        var definitionStore = _definitionStore;
        var historyStore = new ReminderHistoryStore(_tempDir.Paths);

        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => _sharedResolver);
            reminders.WithSettings(new ReminderSettings
            {
                AckTimeout = NetclawAkkaHostingExtensions.ReminderAckTimeout
            });
        });

        builder.StartActors((system, registry, _) =>
        {
            registry.Register<SessionManagerActorKey>(system.DeadLetters);

            // The manager clock and the scheduler clock start at the same instant.
            _timeProvider.SetUtcNow(system.Scheduler.Now);
            var defaults = new EffectivePolicyDefaults(
                DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);
            var reminderManager = system.ActorOf(
                Props.Create(() => new ReminderManagerActor(
                    _sessionPipeline,
                    defaults,
                    new SchedulingConfig(),
                    _timeProvider,
                    definitionStore,
                    historyStore,
                    _notificationSink,
                    NullReminderChannelNotifier.Instance)),
                "reminder-manager-retry-test");

            registry.Register<ReminderManagerActorKey>(reminderManager);
            _sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, reminderManager);
        });
    }

    // Each offset is the time, in seconds after the first due time, at which
    // Akka.Reminders delivers the next attempt. A retry waits 60, 120, 240 and
    // 480 seconds. Akka.Reminders expires an occurrence when the next retry
    // does not fit before the next due time. The series then continues at the
    // next due time.
    [Theory]
    [InlineData(1, new[] { 0, 60, 120, 180, 240 })]
    [InlineData(5, new[] { 0, 60, 180, 300, 360 })]
    [InlineData(15, new[] { 0, 60, 180, 420, 900 })]
    public async Task Short_interval_reminder_stays_enabled_until_the_fifth_consecutive_failure(
        int intervalMinutes,
        int[] attemptOffsetsSeconds)
    {
        Assert.Equal(ReminderManagerActor.FailurePauseThreshold, attemptOffsetsSeconds.Length);
        var id = await ScheduleIntervalReminderAsync($"failing-{intervalMinutes}m", TimeSpan.FromMinutes(intervalMinutes));

        var elapsedSeconds = -(int)FirstFireDelay.TotalSeconds;
        for (var attempt = 1; attempt <= attemptOffsetsSeconds.Length; attempt++)
        {
            var offset = attemptOffsetsSeconds[attempt - 1];
            Advance(TimeSpan.FromSeconds(offset - elapsedSeconds));
            elapsedSeconds = offset;
            await AwaitSettledFailuresAsync(id, attempt);

            var definition = _definitionStore.Get(id);
            Assert.NotNull(definition);
            Assert.Equal(attempt, _sessionPipeline.InvocationCount);
            Assert.Equal(attempt, definition.ConsecutiveFailures);

            if (attempt < ReminderManagerActor.FailurePauseThreshold)
            {
                Assert.True(definition.Enabled, $"The reminder stopped after failure {attempt}.");
                Assert.Null(definition.TerminalOutcome);
                Assert.NotNull((await GetStatusAsync(id)).NextFire);
            }
            else
            {
                Assert.False(definition.Enabled);
                Assert.Equal(ReminderTerminalOutcome.Failed, definition.TerminalOutcome);
            }
        }

        Assert.Single(_notificationSink.Alerts, a =>
            a.Category == AlertType.ReminderAutoDisabled && a.Source == id.Value);
    }

    // The scheduler does not run for more than three intervals, as after a
    // host suspend. Akka.Reminders expires the occurrences that it missed and
    // delivers the current occurrence only. The series then continues.
    [Fact]
    public async Task One_minute_series_fires_one_time_after_a_stall_and_continues()
    {
        var id = await ScheduleIntervalReminderAsync("stalled-1m", TimeSpan.FromMinutes(1));
        Advance(FirstFireDelay);
        await AwaitSettledFailuresAsync(id, 1);

        // 200 seconds after the first due time, the slots at 60 and 120 seconds
        // are stale and the slot at 180 seconds is current.
        Advance(TimeSpan.FromSeconds(200));

        // Akka.Reminders expires the stale slot in one pass and starts a timer
        // with no delay for the current slot. The test scheduler fires that
        // timer only when the clock moves, so each poll moves it by one tick.
        await AwaitAssertAsync(
            () =>
            {
                Advance(TimeSpan.FromTicks(1));
                Assert.Equal(2, CountFailureAlerts(id));
            },
            duration: TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Advance(TimeSpan.FromSeconds(40));
        await AwaitSettledFailuresAsync(id, 3);

        Assert.Equal(3, _sessionPipeline.InvocationCount);
        var definition = _definitionStore.Get(id);
        Assert.NotNull(definition);
        Assert.True(definition.Enabled);
    }

    [Fact]
    public async Task Failed_count_includes_a_failing_one_minute_series_until_it_is_auto_disabled()
    {
        var id = await ScheduleIntervalReminderAsync("counted-1m", TimeSpan.FromMinutes(1));

        var elapsedSeconds = -(int)FirstFireDelay.TotalSeconds;
        foreach (var (attempt, offset) in new[] { 0, 60, 120, 180, 240 }.Select((o, i) => (i + 1, o)))
        {
            Advance(TimeSpan.FromSeconds(offset - elapsedSeconds));
            elapsedSeconds = offset;
            await AwaitSettledFailuresAsync(id, attempt);

            var expectedFailed = attempt < ReminderManagerActor.FailurePauseThreshold ? 1 : 0;
            Assert.Equal(expectedFailed, (await GetHealthAsync()).FailedCount);
        }

        Assert.False(_definitionStore.Get(id)!.Enabled);
    }

    [Fact]
    public async Task Failing_oneshot_ends_failed_and_is_never_pruned()
    {
        var id = await ScheduleOneShotAsync("failing-oneshot");

        // A one-shot has no delivery deadline, so Akka.Reminders keeps retrying
        // (60, 120, 240 and 480 seconds apart) until the fifth failure disables it.
        var elapsedSeconds = -(int)FirstFireDelay.TotalSeconds;
        foreach (var (attempt, offset) in new[] { 0, 60, 180, 420, 900 }.Select((o, i) => (i + 1, o)))
        {
            Advance(TimeSpan.FromSeconds(offset - elapsedSeconds));
            elapsedSeconds = offset;
            await AwaitSettledFailuresAsync(id, attempt);
        }

        var definition = _definitionStore.Get(id);
        Assert.NotNull(definition);
        Assert.False(definition.Enabled);
        Assert.Equal(ReminderTerminalOutcome.Failed, definition.TerminalOutcome);

        // Well past the retention period, a prune keeps it.
        Advance(ReminderManagerActor.TerminalRetention + TimeSpan.FromDays(1));
        var manager = ActorRegistry.For(Sys).Get<ReminderManagerActorKey>();
        manager.Tell(ReminderManagerActor.PruneTerminalReminders.Instance);
        await GetHealthAsync(); // the manager handles messages one at a time, so the prune has finished

        var kept = _definitionStore.Get(id);
        Assert.NotNull(kept);
        Assert.Equal(ReminderTerminalOutcome.Failed, kept.TerminalOutcome);
    }

    private async Task<ReminderId> ScheduleOneShotAsync(string id)
    {
        var now = _timeProvider.GetUtcNow();
        var fireAt = now + FirstFireDelay;
        var definition = new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = id,
            Instructions = "Check status",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = fireAt },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };

        var manager = ActorRegistry.For(Sys).Get<ReminderManagerActorKey>();
        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);
        return definition.Id;
    }

    private Task<ReminderHealthResponse> GetHealthAsync() =>
        ActorRegistry.For(Sys).Get<ReminderManagerActorKey>().Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private async Task<ReminderId> ScheduleIntervalReminderAsync(string id, TimeSpan interval)
    {
        var now = _timeProvider.GetUtcNow();
        var definition = new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = id,
            Instructions = "Check status",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                Interval = interval,
                FireAt = now + FirstFireDelay
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };

        var manager = ActorRegistry.For(Sys).Get<ReminderManagerActorKey>();
        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);
        return definition.Id;
    }

    private void Advance(TimeSpan offset)
    {
        // The manager clock moves first. The scheduler delivers the due
        // occurrences during its own advance.
        _timeProvider.Advance(offset);
        ((TestScheduler)Sys.Scheduler).Advance(offset);
    }

    // The manager emits the failure alert after Akka.Reminders answers the
    // negative acknowledgement. At that point Akka.Reminders holds the timer
    // for the next attempt, so the test can advance the clock again.
    private Task AwaitSettledFailuresAsync(ReminderId id, int expected) =>
        AwaitAssertAsync(
            () => Assert.Equal(expected, CountFailureAlerts(id)),
            duration: TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    private int CountFailureAlerts(ReminderId id) =>
        _notificationSink.Alerts.Count(a =>
            a.Category == AlertType.ReminderExecutionFailed && a.Source == id.Value);

    private Task<ReminderStatusResponse> GetStatusAsync(ReminderId id) =>
        ActorRegistry.For(Sys).Get<ReminderManagerActorKey>().Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(id, OperatorAuthorization),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
}
