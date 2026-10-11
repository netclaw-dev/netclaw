// -----------------------------------------------------------------------
// <copyright file="ReminderDeniedRunSettlementTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Hosting;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// A run in which authorization denied a tool call is acknowledged, not retried: a denial is deterministic,
/// so a retry cannot help. It records a denied history row, leaves the failure count alone, never
/// auto-disables, and alerts once per distinct denial until the reminder next records an ok run.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class ReminderDeniedRunSettlementTests : TestKit, IAsyncDisposable
{
    private const string DeniedText = "Tool access denied: shell_execute needs approval, and nobody can answer a prompt in an unattended run.";

    private readonly TestSessionTempDirectory _tempDir =
        TestSessionTempDirectory.Create(prefix: "netclaw-reminder-denied-", createDirectoryTree: true);
    private readonly TestShardRegionResolver _resolver = new();
    private readonly FakeTimeProvider _timeProvider = new(TimeProvider.System.GetUtcNow());
    private ReminderDefinitionStore _definitionStore = null!;
    private ReminderHistoryStore _historyStore = null!;
    private ReminderManagerActorTests.TestNotificationSink _sink = null!;
    private volatile bool _deny = true;
    private ReminderExecutionActorTests.ScriptedSessionPipeline _pipeline = null!;

    public ReminderDeniedRunSettlementTests(ITestOutputHelper output) : base(output: output) { }

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
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization().WithSerializationVerification();
        _definitionStore = new ReminderDefinitionStore(_tempDir.Paths);
        _historyStore = new ReminderHistoryStore(_tempDir.Paths);
        _sink = new ReminderManagerActorTests.TestNotificationSink();
        _pipeline = new ReminderExecutionActorTests.ScriptedSessionPipeline(sessionId => _deny
            ?
            [
                new ToolResultOutput
                {
                    SessionId = sessionId,
                    CallId = new Netclaw.Tools.ToolCallId("call-d"),
                    ToolName = new Netclaw.Tools.ToolName("shell_execute"),
                    Result = DeniedText,
                    FailureCode = ToolResultOutput.AccessDeniedFailureCode
                },
                new TextOutput("Done another way.") { SessionId = sessionId },
                new TurnCompleted { SessionId = sessionId, TurnNumber = new TurnNumber(1) }
            ]
            :
            [
                new TextOutput("Done.") { SessionId = sessionId },
                new TurnCompleted { SessionId = sessionId, TurnNumber = new TurnNumber(1) }
            ]);

        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => _resolver);
            reminders.WithSettings(new ReminderSettings
            {
                AckTimeout = TimeSpan.FromMinutes(70),
                RetryBackoffBase = TimeSpan.FromMilliseconds(25),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(25),
                MaxDeliveryAttempts = 10
            });
        });

        builder.StartActors((system, registry, _) =>
        {
            registry.Register<SessionManagerActorKey>(system.DeadLetters);
            var defaults = new EffectivePolicyDefaults(DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);
            var manager = system.ActorOf(Props.Create(() => new ReminderManagerActor(
                _pipeline, defaults, new SchedulingConfig(), _timeProvider, _definitionStore, _historyStore,
                _sink, NullReminderChannelNotifier.Instance)), "reminder-manager-denied");
            registry.Register<ReminderManagerActorKey>(manager);
            _resolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, manager);
        });
    }

    private ReminderDefinition CreateDefinition(ReminderSchedule schedule)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = new ReminderId($"denied-{Guid.NewGuid():N}"[..20]),
            Title = "Restart nginx",
            Instructions = "Restart nginx.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = schedule,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private void Fire(ReminderDefinition definition)
        => ActorRegistry.For(Sys).Get<ReminderManagerActorKey>().Tell(new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(definition.Id.Value),
            TimeProvider.System.GetUtcNow(),
            ReminderDeadline.Infinite,
            new ReminderPayload { Id = definition.Id }));

    private async Task AwaitHistoryAsync(ReminderId id, int count)
        => await AwaitAssertAsync(async () =>
            Assert.Equal(count, (await _historyStore.ReadAsync(id, 50)).Count),
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

    private int DeniedAlerts(ReminderId id)
        => _sink.Alerts.Count(a => a.Category == AlertType.ReminderExecutionFailed && a.Source == id.Value);

    [Fact]
    public async Task A_denied_one_shot_runs_once_and_is_kept_as_denied()
    {
        var manager = ActorRegistry.For(Sys).Get<ReminderManagerActorKey>();
        var definition = CreateDefinition(new ReminderSchedule
        {
            Type = ReminderScheduleType.OneShot,
            FireAt = TimeProvider.System.GetUtcNow().AddMilliseconds(100)
        });
        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);

        await AwaitHistoryAsync(definition.Id, 1);
        // The retry backoff here is 25 ms: a nack would have run the model again long before this.
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);

        var history = await _historyStore.ReadAsync(definition.Id, 50);
        var row = Assert.Single(history);
        Assert.Equal("denied", row.Status);
        Assert.Equal(1, _pipeline.InvocationCount);
        var stored = _definitionStore.Get(definition.Id);
        Assert.NotNull(stored); // kept: the history is the only record of the denial
        Assert.Equal(0, stored!.ConsecutiveFailures);
        Assert.Null(stored.TerminalOutcome);
        Assert.Equal(1, DeniedAlerts(definition.Id));
        Assert.DoesNotContain(_sink.Alerts, a => a.Category == AlertType.ReminderAutoDisabled);
    }

    [Fact]
    public async Task A_denied_one_shot_is_not_counted_as_failed_and_is_never_pruned()
    {
        var manager = ActorRegistry.For(Sys).Get<ReminderManagerActorKey>();
        var definition = CreateDefinition(new ReminderSchedule
        {
            Type = ReminderScheduleType.OneShot,
            FireAt = TimeProvider.System.GetUtcNow().AddMilliseconds(100)
        });
        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);
        await AwaitHistoryAsync(definition.Id, 1);
        await AwaitAssertAsync(
            () => Assert.False(_definitionStore.Get(definition.Id)!.Enabled),
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Age the disabled one-shot well past the retention period, then prune.
        var stored = _definitionStore.Get(definition.Id)!;
        _definitionStore.Save(stored with
        {
            UpdatedAtMs = _timeProvider.GetUtcNow().Subtract(ReminderManagerActor.TerminalRetention + TimeSpan.FromDays(30)).ToUnixTimeMilliseconds()
        });
        manager.Tell(ReminderManagerActor.PruneTerminalReminders.Instance);
        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(_definitionStore.Get(definition.Id));
        Assert.Single(await _historyStore.ReadAsync(definition.Id, 50));
        Assert.Equal(0, health.FailedCount);
    }

    [Fact]
    public async Task A_recurring_reminder_stays_enabled_and_alerts_once_until_it_next_runs_ok()
    {
        var definition = CreateDefinition(new ReminderSchedule
        {
            Type = ReminderScheduleType.Interval,
            Interval = TimeSpan.FromDays(7)
        });
        _definitionStore.Save(definition);

        for (var run = 1; run <= 6; run++)
        {
            Fire(definition);
            await AwaitHistoryAsync(definition.Id, run);
        }

        var stored = _definitionStore.Get(definition.Id)!;
        Assert.True(stored.Enabled);
        Assert.Equal(0, stored.ConsecutiveFailures);
        Assert.Null(stored.TerminalOutcome);
        Assert.All(await _historyStore.ReadAsync(definition.Id, 50), r => Assert.Equal("denied", r.Status));
        // Six denied runs, one alert (more than the failure threshold of five, and still enabled).
        Assert.Equal(1, DeniedAlerts(definition.Id));

        _deny = false;
        Fire(definition);
        await AwaitHistoryAsync(definition.Id, 7);
        _deny = true;
        Fire(definition);
        await AwaitHistoryAsync(definition.Id, 8);

        var rows = await _historyStore.ReadAsync(definition.Id, 50);
        Assert.Equal("ok", rows[6].Status);
        Assert.Equal("denied", rows[7].Status);
        Assert.Equal(2, DeniedAlerts(definition.Id));
    }
}
