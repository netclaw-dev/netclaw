// -----------------------------------------------------------------------
// <copyright file="ReminderManagerActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Akka.Streams;
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

[Collection(ReminderActorTestCollection.Name)]
public class ReminderManagerActorTests : TestKit, IAsyncDisposable
{
    private readonly TestSessionTempDirectory _tempDir =
        TestSessionTempDirectory.Create(prefix: "netclaw-reminder-tests-", createDirectoryTree: true);
    private readonly FakeTimeProvider _timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly TestShardRegionResolver _sharedResolver = new();
    private ReminderDefinitionStore _definitionStore = null!;
    private TestNotificationSink _notificationSink = null!;
    private readonly FailingReminderSessionPipeline _sessionPipeline =
        new("persistence recovery failed");

    // Matches the audience the daemon HTTP layer resolves for an Operator caller
    // (see ResolveReminderAuthorizationContext) — these tests call the manager
    // directly, the same way that layer does, so they use the same caller shape.
    private static readonly ReminderAudienceAuthorizationContext OperatorAuthorization =
        new(TrustAudience.Personal, "Operator/test");

    public ReminderManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    // TestKit stops the actor system only after AfterAllAsync returns, and it fails
    // the test when AfterAllAsync takes more than 5 seconds. Delete the directory
    // after TestKit has disposed, and not in AfterAllAsync.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            // Release a held pipeline call after the actor system stops, so
            // the released call cannot start a retry during teardown.
            _sessionPipeline.ReleaseHeldSessions();
            await _tempDir.DisposeAsync();
        }
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSerializationVerification();

        var paths = _tempDir.Paths;
        _definitionStore = new ReminderDefinitionStore(paths);
        _notificationSink = new TestNotificationSink();
        var definitionStore = _definitionStore;
        var historyStore = new ReminderHistoryStore(paths);

        // Wire local reminders with in-memory storage
        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => _sharedResolver);
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

            var reminderManager = system.ActorOf(
                CreateManagerProps(definitionStore, historyStore),
                "reminder-manager-test");

            registry.Register<ReminderManagerActorKey>(reminderManager);
            _sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, reminderManager);
        });
    }

    private Props CreateManagerProps(
        ReminderDefinitionStore definitionStore,
        ReminderHistoryStore historyStore)
    {
        var defaults = new EffectivePolicyDefaults(
            DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);
        return Props.Create(() => new ReminderManagerActor(
            _sessionPipeline,
            defaults,
            new SchedulingConfig(),
            _timeProvider,
            definitionStore,
            historyStore,
            _notificationSink,
            NullReminderChannelNotifier.Instance));
    }

    private async Task<IActorRef> GetManagerAsync()
    {
        var registry = ActorRegistry.For(Sys);
        return registry.Get<ReminderManagerActorKey>();
    }

    [Fact]
    public async Task Schedule_and_list_returns_reminder()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-list", "Check status");
        var authorization = new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test");

        var scheduled = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, Authorization: authorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("test-list", scheduled.Title);
        Assert.NotNull(scheduled.NextFire);

        var list = await manager.Ask<ReminderListResponse>(
            new ListRemindersCommand(OperatorAuthorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(list.Reminders);
        Assert.Equal("test-list", list.Reminders[0].Title);
        Assert.Equal("Check status", list.Reminders[0].Instructions);
    }

    [Fact]
    public async Task Cancel_nonexistent_returns_not_found()
    {
        var manager = await GetManagerAsync();

        var cancelled = await manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(new ReminderId("does-not-exist"), OperatorAuthorization),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(cancelled.Found);
    }

    [Fact]
    public async Task Health_query_returns_scheduled_count()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-health", "Check health");
        var authorization = new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test");

        await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, Authorization: authorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, health.ScheduledCount);
        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(0, health.FailedCount);
    }

    [Fact]
    public async Task Health_query_on_empty_manager_returns_zeros()
    {
        var manager = await GetManagerAsync();

        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, health.ScheduledCount);
        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(0, health.FailedCount);
    }

    [Fact]
    public async Task Status_query_returns_per_reminder_health()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-status", "Check status");
        var authorization = new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test");
        await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, Authorization: authorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var status = await manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(definition.Id, OperatorAuthorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(status.Found);
        Assert.True(status.Enabled);
        Assert.False(status.Executing);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(0, status.SkippedDuplicates);
        Assert.NotNull(status.NextFire);
        Assert.Empty(status.RecentHistory);
    }

    [Fact]
    public async Task Status_query_for_unknown_reminder_returns_not_found()
    {
        var manager = await GetManagerAsync();

        var status = await manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(new ReminderId("does-not-exist"), OperatorAuthorization),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(status.Found);
        Assert.False(status.Enabled);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(0, status.SkippedDuplicates);
        Assert.Empty(status.RecentHistory);
    }

    [Fact]
    public async Task Status_query_reads_durable_failure_count_after_store_reload()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("durable-status", "Check status") with
        {
            ConsecutiveFailures = 3
        };
        _definitionStore.Save(definition);

        var status = await manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(definition.Id, OperatorAuthorization),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(status.Found);
        Assert.Equal(3, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task Failed_enable_keeps_terminal_diagnostics()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("failed-enable", "Failed enable") with
        {
            Enabled = false,
            ConsecutiveFailures = 5,
            TerminalOutcome = ReminderTerminalOutcome.Failed,
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = TimeProvider.System.GetUtcNow().AddMinutes(-1)
            }
        };
        _definitionStore.Save(definition);

        var response = await manager.Ask<ReminderStateResponse>(
            new EnableReminderCommand(definition.Id),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(response.Found);
        Assert.False(response.Enabled);
        Assert.NotNull(response.ErrorMessage);
        var stored = _definitionStore.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.False(stored!.Enabled);
        Assert.Equal(5, stored.ConsecutiveFailures);
        Assert.Equal(ReminderTerminalOutcome.Failed, stored.TerminalOutcome);
    }

    [Fact]
    public async Task Manager_restart_preserves_definition_and_occurrence_state()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("restart-state", "Restart state") with
        {
            ConsecutiveFailures = 3
        };
        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);

        Watch(manager);
        Sys.Stop(manager);
        await ExpectTerminatedAsync(
            manager,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        var paths = _tempDir.Paths;
        var restarted = Sys.ActorOf(
            CreateManagerProps(
                new ReminderDefinitionStore(paths),
                new ReminderHistoryStore(paths)),
            "reminder-manager-restarted");
        ActorRegistry.For(Sys).Register<ReminderManagerActorKey>(restarted, overwrite: true);
        _sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, restarted);

        var status = await restarted.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(definition.Id, OperatorAuthorization),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(status.Found);
        Assert.True(status.Enabled);
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.NotNull(status.NextFire);
        Assert.Equal("Pending", status.Occurrence?.CompletionStatus);
    }

    [Fact]
    public async Task Reconcile_ends_recent_legacy_terminal_oneshots_and_keeps_ambiguous_oneshots()
    {
        var manager = await GetManagerAsync();
        var now = TimeProvider.System.GetUtcNow();

        // Drain PreStart's Self.Tell(ReconcileReminders) AND confirm with our own.
        // ActorOf does not block until PreStart completes — the test's Ask can
        // arrive before PreStart's Self.Tell enqueues the reconcile. Sending two
        // reconcile Asks guarantees that both PreStart's reconcile and our barrier
        // have processed before we write the zombie to the store.
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Write a zombie one-shot directly to the store AFTER startup reconcile:
        // fire time in the past, still enabled, no Akka.Reminders schedule
        var zombie = new ReminderDefinition
        {
            Id = new ReminderId("zombie-oneshot"),
            Title = "Expired one-shot",
            Instructions = "This already fired",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(-1)
            },
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddHours(-2)
        };
        _definitionStore.Save(zombie);
        var historyStore = new ReminderHistoryStore(_tempDir.Paths);
        await historyStore.AppendAsync(
            zombie.Id,
            new HistoryRecord(now.AddMinutes(-30), false, 100, "session-1", "recovery failed"));
        var completed = zombie with
        {
            Id = new ReminderId("completed-old"),
            Enabled = false,
            TerminalOutcome = ReminderTerminalOutcome.Completed
        };
        var failed = zombie with
        {
            Id = new ReminderId("failed-old"),
            Enabled = false,
            ConsecutiveFailures = ReminderManagerActor.FailurePauseThreshold,
            TerminalOutcome = ReminderTerminalOutcome.Failed
        };
        _definitionStore.Save(completed);
        _definitionStore.Save(failed);
        await historyStore.AppendAsync(completed.Id, new HistoryRecord(now, true, 100, "session-1", null));
        await historyStore.AppendAsync(failed.Id, new HistoryRecord(now, false, 100, "session-1", "failed"));

        // Confirm it shows up as scheduled
        var healthBefore = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, healthBefore.ScheduledCount);

        // Trigger reconciliation and wait for completion ack
        var reconcileResult = await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, reconcileResult.SoftDeletedOneShots);

        // The missing occurrence status is ambiguous. Reconciliation must keep
        // the definition and its history for operator review. See issue #1803.
        var afterReconcile = _definitionStore.Get(new ReminderId("zombie-oneshot"));
        Assert.NotNull(afterReconcile);
        Assert.True(afterReconcile!.Enabled);
        Assert.Null(afterReconcile.TerminalOutcome);
        Assert.Single(await historyStore.ReadAsync(zombie.Id, 10));
        Assert.Null(_definitionStore.GetTombstone(zombie.Id));

        // A legacy one-shot with a recorded outcome ends. Its definition goes, its tombstone keeps the
        // outcome, and its history stays for the retention period because it ended recently.
        Assert.Null(_definitionStore.Get(completed.Id));
        var completedTombstone = _definitionStore.GetTombstone(completed.Id);
        Assert.NotNull(completedTombstone);
        Assert.Equal(ReminderTerminalOutcome.Completed, completedTombstone!.Outcome);
        Assert.Null(completedTombstone.Instructions);
        Assert.Single(await historyStore.ReadAsync(completed.Id, 10));

        Assert.Null(_definitionStore.Get(failed.Id));
        var failedTombstone = _definitionStore.GetTombstone(failed.Id);
        Assert.NotNull(failedTombstone);
        Assert.Equal(ReminderTerminalOutcome.Failed, failedTombstone!.Outcome);
        Assert.Equal(failed.Instructions, failedTombstone.Instructions);
        Assert.Single(await historyStore.ReadAsync(failed.Id, 10));
    }

    [Theory]
    [InlineData("sweep")]
    [InlineData("reconcile")]
    public async Task Neither_the_sweep_nor_the_reconcile_deletes_a_recurring_reminder_or_its_history(string trigger)
    {
        var manager = await GetManagerAsync();
        await DrainStartupReconcileAsync(manager);
        var historyStore = new ReminderHistoryStore(_tempDir.Paths);
        // Older than the retention period, and with every terminal marker that the old version wrote.
        var stale = _timeProvider.GetUtcNow() - ReminderManagerActor.TerminalRetention - TimeSpan.FromDays(8);
        var survivors = new[]
        {
            SaveTerminalFixture("stale-failed-cron", ReminderScheduleType.Cron, false, ReminderTerminalOutcome.Failed, stale),
            SaveTerminalFixture("stale-completed-cron", ReminderScheduleType.Cron, false, ReminderTerminalOutcome.Completed, stale),
            SaveTerminalFixture("stale-cancelled-cron", ReminderScheduleType.Cron, false, null, stale)
        };
        foreach (var d in survivors)
            await historyStore.AppendAsync(d.Id, new HistoryRecord(stale, true, 1, "s", null));

        if (trigger == "sweep")
        {
            // ReceiveAsync handlers run one at a time, so the health reply proves the sweep finished.
            manager.Tell(ReminderManagerActor.PruneTerminalReminders.Instance);
            await manager.Ask<ReminderHealthResponse>(
                GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        else
        {
            await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
                ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        foreach (var d in survivors)
        {
            Assert.True(_definitionStore.Get(d.Id) is not null, $"{d.Id.Value} was deleted");
            Assert.Single(await historyStore.ReadAsync(d.Id, 10));
            Assert.Null(_definitionStore.GetTombstone(d.Id));
        }
    }

    [Theory]
    [InlineData(TrustAudience.Team, TrustAudience.Team, true)]
    [InlineData(TrustAudience.Public, TrustAudience.Personal, true)]
    [InlineData(TrustAudience.Personal, TrustAudience.Team, false)]
    public async Task Save_authorizes_requested_audience_against_source_authority(
        TrustAudience requestedAudience,
        TrustAudience sourceAudience,
        bool shouldSucceed)
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition($"audience-{requestedAudience}-{sourceAudience}", "Check audience") with
        {
            Audience = requestedAudience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(requestedAudience)
        };

        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(sourceAudience, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(shouldSucceed, response.Success);
        if (!shouldSucceed)
            Assert.Contains("exceeds creator authority", response.ErrorMessage);
    }

    [Fact]
    public async Task Save_rejects_missing_authorization_context()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("missing-auth", "Check auth");

        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(ReminderSaveError.Validation, response.Error);
        Assert.Contains("authorization context is required", response.ErrorMessage);
    }

    [Fact]
    public async Task Save_accepts_expiration_for_oneshot_reminders()
    {
        var manager = await GetManagerAsync();
        var now = TimeProvider.System.GetUtcNow();
        var definition = CreateDefinition("oneshot-expire", "Check expiry") with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            ExpiresAt = now.AddHours(2)
        };

        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(definition.ExpiresAt, _definitionStore.Get(definition.Id)?.ExpiresAt);
    }

    [Fact]
    public async Task Save_explicit_audience_within_source_authority_is_persisted()
    {
        // Audience is now required non-nullable on ReminderDefinition (#994 type-stiffening).
        // The definition specifies an explicit audience; the manager persists it when it does
        // not exceed the source authority. Here the definition requests Public and source
        // authority is Team — Public <= Team, so it should succeed and Public is stored.
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("explicit-audience", "Check explicit audience") with
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public
        };

        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);

        var saved = _definitionStore.Get(response.Id);
        Assert.NotNull(saved);
        Assert.Equal(TrustAudience.Public, saved!.Audience);
    }

    [Fact]
    public async Task Save_rejects_boundary_that_exceeds_requested_audience()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("public-boundary-mismatch", "Check mismatch") with
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Personal
        };

        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Personal, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(ReminderSaveError.Validation, response.Error);
        Assert.Contains("not allowed for audience 'public'", response.ErrorMessage);
    }

    [Fact]
    public async Task Save_allows_narrower_boundary_than_requested_audience()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("narrow-boundary", "Check narrow boundary") with
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Public
        };

        var response = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Personal, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);

        var saved = _definitionStore.Get(response.Id);
        Assert.NotNull(saved);
        Assert.Equal(TrustBoundary.Public, saved!.Boundary);
    }

    [Fact]
    public async Task Startup_emits_alert_for_legacy_reminder_missing_trust_fields()
    {
        using var directory = new DisposableTempDir();
        const string reminderId = "legacy-reminder-alert";
        var now = TimeProvider.System.GetUtcNow();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        var filePath = Path.Combine(paths.RemindersDirectory, $"{Uri.EscapeDataString(reminderId)}.json");
        File.WriteAllText(filePath, $$"""
            {
              "id": "{{reminderId}}",
              "title": "Legacy Reminder",
              "instructions": "Check status",
              "delivery": { "kind": "None" },
              "schedule": { "type": "OneShot", "fireAtMs": {{now.AddHours(1).ToUnixTimeMilliseconds()}} },
              "enabled": true,
              "createdBy": "test",
              "createdAtMs": {{now.ToUnixTimeMilliseconds()}},
              "updatedAtMs": {{now.ToUnixTimeMilliseconds()}}
            }
            """);

        var store = new ReminderDefinitionStore(paths);
        var sink = new TestNotificationSink();
        var pipeline = new SessionPipeline(
            Sys,
            new RequiredActor<SessionManagerActorKey>(ActorRegistry.For(Sys)),
            new Netclaw.Actors.Protocol.TestSessionStorageResolver(paths));
        var defaults = new EffectivePolicyDefaults(
            DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);

        var manager = Sys.ActorOf(
            Props.Create(() => new ReminderManagerActor(
                pipeline,
                defaults,
                new SchedulingConfig(),
                TimeProvider.System,
                store,
                new ReminderHistoryStore(paths),
                sink,
                NullReminderChannelNotifier.Instance)),
            "legacy-reminder-alert-manager");

        await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Contains(sink.Alerts, alert =>
            alert.Category == AlertType.ReminderSchemaDropped
            && alert.Summary.Contains(reminderId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Anchor end-to-end test for Mode B reminder re-entry. Exercises the
    /// full Netclaw-owned chain: manager branching on <c>OriginChannelType</c>,
    /// execution actor resolving the gateway via <c>ActorRegistry</c>,
    /// dispatching <c>DeliverTrustedSessionTurn</c> via <c>Ask&lt;CommandAck&gt;</c>,
    /// and calling <c>_client.AckAsync(envelope)</c> on success. The Slack
    /// and SignalR gateway routing chains are stubbed with a probe actor
    /// registered under the marker keys — their internal lookup-or-create
    /// hierarchy is covered by the unit tests in <c>SlackActorHierarchyTests</c>
    /// and <c>SignalRMessageExtractorTests</c>.
    /// </summary>
    [Fact]
    public async Task Mode_B_reminder_dispatches_to_resolved_gateway_and_completes_on_CommandAck()
    {
        var manager = await GetManagerAsync();

        // Register a probe under SlackGatewayActorKey that auto-replies
        // CommandAck to any DeliverTrustedSessionTurn — simulating the
        // happy path where the full Slack routing chain + session +
        // pipeline have successfully processed the trusted turn.
        var gatewayProbe = CreateTestProbe("fake-slack-gateway");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-slack-gateway");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        // Persist a Mode B reminder definition with a realistic Slack
        // session id and OriginChannelType = Slack.
        var now = TimeProvider.System.GetUtcNow();
        var definition = new ReminderDefinition
        {
            Id = new ReminderId("mode-b-anchor"),
            Title = "Mode B Anchor",
            Instructions = "Check PR #123",
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "C0123ABC/1712000000.000001",
                OriginChannelType = ChannelType.Slack
            },
            DeliveryInstructions = "Reply in this session with the result.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.TrustedInstance,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
        _definitionStore.Save(definition);

        // Synthesize an envelope as if Akka.Reminders had fired it and
        // Tell the manager directly — this is what the scheduler does
        // internally at fire time.
        var envelope = new ReminderEnvelope<ReminderPayload>(
            entity: new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            key: new ReminderKey(definition.Id.Value),
            dueTimeUtc: definition.Schedule.FireAt!.Value,
            deadline: ReminderDeadline.Infinite,
            message: new ReminderPayload { Id = definition.Id });

        manager.Tell(envelope);

        // Probe receives the DeliverTrustedSessionTurn routed via the
        // execution actor's gateway resolution.
        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C0123ABC/1712000000.000001", delivered.SessionId.Value);
        Assert.Contains("Check PR #123", delivered.Content);
        Assert.Equal(ChannelType.Slack, delivered.Source.ChannelType);
        Assert.Equal(TrustAudience.Team, delivered.Source.Audience);
        Assert.Equal(TrustBoundary.TrustedInstance, delivered.Source.Boundary);
        Assert.NotNull(delivered.Source.ReminderId);
        Assert.StartsWith("mode-b-anchor:", delivered.Source.ReminderId!.Value.Value);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, delivered.Source.Principal);
        Assert.Equal("reminder", delivered.Source.Provenance.SourceKind?.Value);

        // Probe receiving the DeliverTrustedSessionTurn is the anchor
        // assertion: it proves the manager branched on OriginChannelType,
        // spawned the execution actor with the envelope, resolved the
        // gateway via ActorRegistry, and issued the Ask. The probe's
        // CommandAck reply + the subsequent _client.AckAsync path is
        // exercised end-to-end here too (execution actor didn't crash
        // or persist a failure on the reminder definition); any
        // failure in that tail would surface as a reminder execution
        // failure alert via the notification sink, which this test would
        // see on the next Ask to the manager if it happened.
    }

    [Fact]
    public async Task Mode_B_discord_reminder_dispatches_to_resolved_gateway_and_completes_on_CommandAck()
    {
        var manager = await GetManagerAsync();

        var gatewayProbe = CreateTestProbe("fake-discord-gateway");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-discord-gateway");
        ActorRegistry.For(Sys).Register<DiscordGatewayActorKey>(autoAckRef);

        var now = TimeProvider.System.GetUtcNow();
        var definition = new ReminderDefinition
        {
            Id = new ReminderId("mode-b-discord-anchor"),
            Title = "Mode B Discord Anchor",
            Instructions = "Check incident status",
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "129847561203948576/130111223344556677",
                OriginChannelType = ChannelType.Discord
            },
            DeliveryInstructions = "Reply in this session with the result.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.TrustedInstance,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
        _definitionStore.Save(definition);

        var envelope = new ReminderEnvelope<ReminderPayload>(
            entity: new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            key: new ReminderKey(definition.Id.Value),
            dueTimeUtc: definition.Schedule.FireAt!.Value,
            deadline: ReminderDeadline.Infinite,
            message: new ReminderPayload { Id = definition.Id });

        manager.Tell(envelope);

        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("129847561203948576/130111223344556677", delivered.SessionId.Value);
        Assert.Contains("Check incident status", delivered.Content);
        Assert.Equal(ChannelType.Discord, delivered.Source.ChannelType);
        Assert.Equal(TrustAudience.Team, delivered.Source.Audience);
        Assert.Equal(TrustBoundary.TrustedInstance, delivered.Source.Boundary);
        Assert.NotNull(delivered.Source.ReminderId);
    }

    // The session dedups redeliveries by the delivery key, so the key MUST be
    // built from the envelope's scheduled fire time (identical across
    // redeliveries) — not the per-execution dispatch wall-clock, which drifts
    // and would let a redelivery slip past dedup and deliver twice.
    [Fact]
    public async Task CurrentSession_delivery_key_is_built_from_envelope_fire_time()
    {
        var manager = await GetManagerAsync();

        var gatewayProbe = CreateTestProbe("stable-key-gateway");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-stable-key");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        // A distinctive fire time far from "now": a key built from the dispatch
        // wall-clock would not match it. The definition has the same fire time, because a
        // one-shot fire runs only the occurrence of the current definition.
        var fireTime = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var definition = CreateCurrentSessionDefinition("stable-key", deliveryRequired: true) with
        {
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = fireTime }
        };
        _definitionStore.Save(definition);

        var envelope = new ReminderEnvelope<ReminderPayload>(
            entity: new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            key: new ReminderKey(definition.Id.Value),
            dueTimeUtc: fireTime,
            deadline: ReminderDeadline.Infinite,
            message: new ReminderPayload { Id = definition.Id });

        manager.Tell(envelope);

        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new ReminderId($"{definition.Id}:{fireTime.ToUnixTimeMilliseconds()}"), delivered.Source.ReminderId);
    }

    // Rule 3: admission ends the reminder system's job. The session acknowledges the
    // fire once it has stored the input as a pending input. A post that the session
    // makes later is not part of the run. The flag asks for more only for a Channel run.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentSession_reminder_succeeds_at_admission_whether_or_not_delivery_is_required(
        bool deliveryRequired)
    {
        var manager = await GetManagerAsync();
        var gatewayProbe = CreateTestProbe("admission-gateway");
        var gateway = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            $"auto-ack-admission-{deliveryRequired}");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(gateway);

        // An interval schedule keeps the definition after the run, so the test can read it.
        var definition = CreateCurrentSessionDefinition(
            $"admission-{deliveryRequired}".ToLowerInvariant(), deliveryRequired) with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                FireAt = _timeProvider.GetUtcNow().AddMinutes(5),
                IntervalTicks = TimeSpan.FromMinutes(5).Ticks
            },
            ConsecutiveFailures = 2
        };
        _definitionStore.Save(definition);

        manager.Tell(CreateEnvelope(definition));
        await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // No delivery result follows the acknowledgement. The run must still end in success.
        var historyStore = new ReminderHistoryStore(_tempDir.Paths);
        await AwaitAssertAsync(async () =>
        {
            var record = Assert.Single(await historyStore.ReadAsync(definition.Id, 10));
            Assert.Equal("ok", record.Status);
            Assert.Equal(0, _definitionStore.Get(definition.Id)!.ConsecutiveFailures);
            var health = await manager.Ask<ReminderHealthResponse>(
                GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(0, health.ActiveExecutions);
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_notificationSink.Alerts, alert =>
            alert.Category == AlertType.ReminderExecutionFailed && alert.Source == definition.Id.Value);
    }

    // The part of the old wait that stays: a session that refuses the fire at admission fails the run.
    // The manager then nacks, and Akka.Reminders redelivers the fire.
    [Fact]
    public async Task CurrentSession_reminder_fails_when_the_session_refuses_the_fire()
    {
        var manager = await GetManagerAsync();
        var nackGateway = Sys.ActorOf(Props.Create(() => new NackTrustedGateway("session is draining")), "nack-gateway");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(nackGateway);

        var definition = CreateCurrentSessionDefinition("admission-refused", deliveryRequired: true);
        _definitionStore.Save(definition);

        manager.Tell(CreateEnvelope(definition));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_notificationSink.Alerts, alert =>
                alert.Category == AlertType.ReminderExecutionFailed
                && alert.Source == definition.Id.Value
                && alert.Summary.Contains("session is draining", StringComparison.OrdinalIgnoreCase));
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reconcile_disables_expired_recurring_reminders()
    {
        var manager = await GetManagerAsync();
        var now = TimeProvider.System.GetUtcNow();

        // Drain PreStart reconcile
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Write an expired interval reminder directly to the store
        var expired = new ReminderDefinition
        {
            Id = new ReminderId("expired-interval"),
            Title = "Expired interval check",
            Instructions = "This should be disabled",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                Interval = TimeSpan.FromMinutes(30),
                FireAt = now.AddMinutes(30)
            },
            ExpiresAt = now.AddHours(-1),
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        };
        _definitionStore.Save(expired);

        var reconcileResult = await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, reconcileResult.DisabledExpired);

        // Verify definition is still on disk (soft-disable, not delete) but disabled
        var afterReconcile = _definitionStore.Get(new ReminderId("expired-interval"));
        Assert.NotNull(afterReconcile);
        Assert.False(afterReconcile!.Enabled);
    }

    [Fact]
    public async Task Expired_reminder_disabled_on_fire_without_executing()
    {
        var manager = await GetManagerAsync();
        var now = _timeProvider.GetUtcNow();

        // Drain PreStart reconcile
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Write an interval reminder that just expired
        var expired = new ReminderDefinition
        {
            Id = new ReminderId("just-expired"),
            Title = "Just expired check",
            Instructions = "This should not execute",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                Interval = TimeSpan.FromMinutes(20),
                FireAt = now
            },
            ExpiresAt = now.AddSeconds(-1),
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        };
        _definitionStore.Save(expired);

        // Fire the reminder
        manager.Tell(CreateEnvelope("just-expired"));

        // Wait for the fire to be processed — the reminder should be disabled, not executed
        await AwaitAssertAsync(async () =>
        {
            var stored = _definitionStore.Get(new ReminderId("just-expired"));
            Assert.NotNull(stored);
            Assert.False(stored!.Enabled);
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Verify no execution was started
        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(0, health.ActiveExecutions);
    }

    [Fact]
    public async Task Recurring_occurrence_starts_even_while_other_reminders_are_running()
    {
        var manager = await GetManagerAsync();

        // Drain PreStart reconcile
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Hold three Mode A runs in flight (the historical capacity limit). A Mode A run
        // creates its own session, so the fixture pipeline keeps it in flight until
        // DisposeAsync releases the hold. A Mode B run would end at admission and could
        // not hold a slot. Every run in this test shares the "held-" prefix, so the one
        // hold also keeps the recurring occurrence below in flight.
        // Save before dispatch so filesystem latency cannot consume any test
        // timing window while execution slots are being filled.
        _sessionPipeline.HoldSessions("reminder/held-");
        var held = new List<ReminderDefinition>();
        for (var i = 0; i < 3; i++)
        {
            var id = $"held-{i}";
            var definition = CreateDefinition("held", "Stay in flight") with
            {
                Id = new ReminderId(id),
                Title = id,
                Delivery = new ReminderDelivery { Kind = DeliveryKind.None }
            };
            _definitionStore.Save(definition);
            held.Add(definition);
        }

        foreach (var definition in held)
            manager.Tell(CreateEnvelope(definition));

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(3, _sessionPipeline.InvocationCount);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var invocationCount = _sessionPipeline.InvocationCount;
        var now = TimeProvider.System.GetUtcNow();
        var recurringId = "held-recurring";

        // The hold also keeps the recurring occurrence in flight. The fixture pipeline
        // always fails, so a free occurrence retries every 25 ms and the manager
        // disables the reminder about 125 ms after the first fire. The Enabled
        // read below then races that retry loop.
        var recurringReminder = new ReminderDefinition
        {
            Id = new ReminderId(recurringId),
            Title = "Concurrent recurring reminder",
            Instructions = "Must start even while other reminders are executing",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                Interval = TimeSpan.FromHours(2),
                FireAt = now.AddMilliseconds(100)
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                recurringReminder,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);

        await AwaitAssertAsync(async () =>
        {
            var status = await manager.Ask<ReminderStatusResponse>(
                new GetReminderStatusQuery(recurringReminder.Id, OperatorAuthorization),
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
            // The historical capacity gate would have skipped this occurrence
            // (SkippedDuplicates == 1) without starting an execution. With the
            // cap removed the occurrence must be dispatched, not skipped.
            Assert.Equal(0, status.SkippedDuplicates);
            Assert.True(_sessionPipeline.InvocationCount > invocationCount,
                "Expected the recurring reminder to be executed by the pipeline.");
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Three blocking executions plus the recurring occurrence run together.
        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(4, health.ActiveExecutions);

        var stored = _definitionStore.Get(recurringReminder.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.Enabled);
    }

    private ReminderDefinition SaveTerminalFixture(
        string id,
        ReminderScheduleType type,
        bool enabled,
        ReminderTerminalOutcome? outcome,
        DateTimeOffset updated)
    {
        var d = CreateCronDefinition(id, "0 0 1 1 *") with
        {
            Id = new ReminderId(id),
            Schedule = type == ReminderScheduleType.OneShot
                ? new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = updated.AddHours(-1) }
                : new ReminderSchedule { Type = ReminderScheduleType.Cron, CronExpression = "0 0 1 1 *" },
            Enabled = enabled,
            ConsecutiveFailures = outcome == ReminderTerminalOutcome.Failed ? ReminderManagerActor.FailurePauseThreshold : 0,
            TerminalOutcome = outcome,
            UpdatedAtMs = updated.ToUnixTimeMilliseconds()
        };
        _definitionStore.Save(d);
        return d;
    }

    [Fact]
    public async Task Reminders_that_are_paused_or_cancelled_stop_counting_as_failed_but_stay_listed()
    {
        var manager = await GetManagerAsync();
        await DrainStartupReconcileAsync(manager);
        var stale = _timeProvider.GetUtcNow() - TimeSpan.FromDays(30);
        SaveTerminalFixture("paused-cron", ReminderScheduleType.Cron, false, ReminderTerminalOutcome.Failed, stale);
        _definitionStore.Save(SaveTerminalFixture("cancelled-failing", ReminderScheduleType.Cron, false, null, stale) with
        {
            ConsecutiveFailures = 2
        });
        _definitionStore.Save(SaveTerminalFixture("failing-enabled", ReminderScheduleType.Cron, true, null, stale) with
        {
            ConsecutiveFailures = 2
        });

        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, health.FailedCount);
        Assert.Equal(3, _definitionStore.List().Count(d => d.Id.Value is "paused-cron" or "cancelled-failing" or "failing-enabled"));
    }

    [Fact]
    public async Task List_reminders_filter_all_names_a_completed_oneshot_completed()
    {
        var manager = await GetManagerAsync();
        await DrainStartupReconcileAsync(manager);
        _definitionStore.Save(SaveTerminalFixture(
            "listed-completed", ReminderScheduleType.OneShot, false, ReminderTerminalOutcome.Completed, _timeProvider.GetUtcNow()) with
        {
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public
        });
        var tool = new ListRemindersTool(manager, new SchedulingConfig { Enabled = true });

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Filter"] = "all" },
            TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("Status: completed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prune_timer_is_started_with_the_actor()
    {
        var defaults = new EffectivePolicyDefaults(
            DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);
        var actor = ActorOfAsTestActorRef<ReminderManagerActor>(
            Props.Create(() => new ReminderManagerActor(
                _sessionPipeline, defaults, new SchedulingConfig { Enabled = true }, _timeProvider,
                _definitionStore, new ReminderHistoryStore(_tempDir.Paths), _notificationSink,
                NullReminderChannelNotifier.Instance)),
            "reminder-manager-timer-test");

        Assert.True(actor.UnderlyingActor.Timers.IsTimerActive(ReminderManagerActor.TerminalPruneTimerKey));
        Assert.Equal(TimeSpan.FromHours(12), ReminderManagerActor.TerminalPruneInterval);
    }

    [Fact]
    public async Task Failed_oneshot_id_can_be_created_again_with_create_only_and_a_fresh_history()
    {
        var manager = await GetManagerAsync();
        await DrainStartupReconcileAsync(manager);
        var historyStore = new ReminderHistoryStore(_tempDir.Paths);
        var now = _timeProvider.GetUtcNow();
        var id = new ReminderId("reuse-failed");
        var ended = CreateDefinition("reuse-failed", "Check it again") with
        {
            Id = id,
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = now.AddHours(-1) }
        };
        _definitionStore.SaveTombstone(new ReminderTombstone
        {
            Id = id,
            Title = ended.Title,
            Audience = TrustAudience.Team,
            FireAtMs = ended.Schedule.FireAtMs!.Value,
            Outcome = ReminderTerminalOutcome.Failed,
            EndedAtMs = now.ToUnixTimeMilliseconds(),
            LastError = "model endpoint is not available",
            Instructions = ended.Instructions
        });
        await historyStore.AppendAsync(id, new HistoryRecord(now, false, 1, "old-session", "failed"));

        // A Failed one-shot kept its id under CreateOnly before it was deleted at the end. Now the id is free.
        var recreated = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                ended with
                {
                    Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = now.AddHours(2) }
                },
                ReminderWriteMode.CreateOnly,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(recreated.Success, recreated.ErrorMessage);
        Assert.Empty(await historyStore.ReadAsync(id, 10));
        Assert.Null(_definitionStore.GetTombstone(id));
        Assert.True(_definitionStore.Get(id)!.Enabled);
    }

    [Fact]
    public async Task Exact_active_delivery_attempt_is_ignored()
    {
        var manager = await GetManagerAsync();

        // NeverReplyGateway accepts the turn but never sends CommandAck —
        // this keeps the first execution actor alive (its Ask is pending)
        // so the reminder ID stays in _activeExecutions when the second
        // envelope arrives.
        var deliveryProbe = CreateTestProbe("delivery-probe");
        var slowGateway = Sys.ActorOf(
            Props.Create(() => new NeverReplyGateway(deliveryProbe.Ref)),
            "never-reply-gateway");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(slowGateway);

        var definition = CreateCurrentSessionDefinition("dup-guard-test", deliveryRequired: false);
        _definitionStore.Save(definition);

        var envelope = CreateEnvelope(definition);

        // Both envelopes go into the actor's mailbox before either is processed,
        // so the second always arrives while the first execution is still in flight.
        manager.Tell(envelope);
        manager.Tell(envelope);

        // Exactly one delivery should reach the gateway.
        await deliveryProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await deliveryProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        // The active execution remains the sole settlement owner.
        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, health.ActiveExecutions);

        // No failure alert — duplicate skip is silent from an alert standpoint.
        Assert.DoesNotContain(_notificationSink.Alerts, a =>
            a.Category == AlertType.ReminderExecutionFailed && a.Source == definition.Id.Value);
    }

    /// <summary>
    /// Regression test for the duplicate-ack over-alert: when the same occurrence
    /// is delivered and settled twice (e.g. redelivered after the first ack),
    /// the second <c>AckAsync</c> returns <c>NotFound</c> because the occurrence
    /// is no longer awaiting ack. That is an idempotent no-op — it must NOT emit
    /// a <c>reminder.settlement.failed</c> alert.
    /// </summary>
    [Fact]
    public async Task Duplicate_ack_of_already_settled_occurrence_does_not_emit_settlement_failed_alert()
    {
        var manager = await GetManagerAsync();

        var gatewayProbe = CreateTestProbe("dup-settlement-gateway");
        var gateway = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-dup-settlement-gateway");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(gateway);

        // Interval schedule so the definition survives the first successful
        // settlement (OneShot definitions are deleted on success). Using the
        // shared helper with deliveryRequired: false so the execution actor
        // settles on CommandAck without waiting for a delivery result.
        var now = _timeProvider.GetUtcNow();
        var definition = CreateCurrentSessionDefinition("dup-settlement", deliveryRequired: false) with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                FireAt = now.AddMinutes(5),
                IntervalTicks = TimeSpan.FromMinutes(5).Ticks
            }
        };
        _definitionStore.Save(definition);

        var envelope = new ReminderEnvelope<ReminderPayload>(
            entity: new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            key: new ReminderKey(definition.Id.Value),
            dueTimeUtc: now,
            deadline: ReminderDeadline.Infinite,
            message: new ReminderPayload { Id = definition.Id });

        // First delivery settles normally: the occurrence is awaiting ack and
        // AckAsync returns Success.
        manager.Tell(envelope);
        await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Wait until the first settlement is fully complete (the manager's
        // AckAsync has returned, so the scheduler no longer awaits an ack for
        // this occurrence). Health shows zero active executions once the
        // settlement's finally block has run.
        await AwaitAssertAsync(async () =>
        {
            var health = await manager.Ask<ReminderHealthResponse>(
                GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, health.ActiveExecutions);
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Replay the exact same occurrence. The scheduler has already settled it,
        // so the second AckAsync returns NotFound. This must not raise an alert.
        // Advance the fake clock so the second execution actor gets a unique name
        // (StartExecution derives the actor name from startedAt).
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        manager.Tell(envelope);
        await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await AwaitAssertAsync(async () =>
        {
            var health = await manager.Ask<ReminderHealthResponse>(
                GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, health.ActiveExecutions);
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_notificationSink.Alerts, a =>
            a.Category == AlertType.ReminderExecutionFailed
            && a.Source == definition.Id.Value
            && a.Summary.Contains("settlement", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unsafe_acknowledgement_lease_does_not_start_execution()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("unsafe-lease", "Unsafe lease");
        _definitionStore.Save(definition);
        var invocationCount = _sessionPipeline.InvocationCount;
        var now = TimeProvider.System.GetUtcNow();
        var envelope = new ReminderEnvelope<ReminderPayload>(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(definition.Id.Value),
            now,
            new ReminderDeadline(now.AddMinutes(60)),
            new ReminderPayload { Id = definition.Id });
        var controlProbe = CreateTestProbe("unsafe-lease-control");

        controlProbe.Send(manager, envelope);
        controlProbe.Send(manager, GetReminderHealthQuery.Instance);
        var health = await controlProbe.ExpectMsgAsync<ReminderHealthResponse>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(invocationCount, _sessionPipeline.InvocationCount);
    }

    // The real scheduler makes each envelope here, so these cases also pin the
    // Akka.Reminders deadline contract: an interval occurrence that has no later
    // retry carries its next due time as the deadline.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public async Task Scheduled_interval_occurrence_starts_execution(int intervalMinutes)
    {
        var manager = await GetManagerAsync();
        var invocationCount = _sessionPipeline.InvocationCount;
        var definition = CreateIntervalDefinition(
            $"interval-{intervalMinutes}m",
            TimeSpan.FromMinutes(intervalMinutes),
            fireAt: TimeProvider.System.GetUtcNow().AddMilliseconds(100));

        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);

        await AwaitAssertAsync(async () =>
        {
            var status = await manager.Ask<ReminderStatusResponse>(
                new GetReminderStatusQuery(definition.Id, OperatorAuthorization),
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, status.SkippedDuplicates);
            Assert.True(_sessionPipeline.InvocationCount > invocationCount,
                "Expected the interval occurrence to start an execution.");
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(60)]
    public async Task Interval_occurrence_starts_until_its_next_due_time(int intervalMinutes)
    {
        var manager = await GetManagerAsync();
        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var due = _timeProvider.GetUtcNow();
        var definition = CreateIntervalDefinition($"current-{intervalMinutes}m", interval, fireAt: due);
        _definitionStore.Save(definition);
        var invocationCount = _sessionPipeline.InvocationCount;

        // One second remains before the next due time.
        _timeProvider.Advance(interval - TimeSpan.FromSeconds(1));
        manager.Tell(CreateOccurrenceEnvelope(definition, due, due + interval));

        await AwaitAssertAsync(
            () => Assert.True(_sessionPipeline.InvocationCount > invocationCount),
            duration: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, await GetSkippedOccurrenceCountAsync(manager, definition.Id));
    }

    // A daemon that was down, or a manager that was busy, must not run an
    // occurrence that a later occurrence has replaced.
    [Fact]
    public async Task Interval_occurrence_at_its_next_due_time_is_skipped()
    {
        var manager = await GetManagerAsync();
        var interval = TimeSpan.FromMinutes(1);
        var due = _timeProvider.GetUtcNow();
        var definition = CreateIntervalDefinition("stale-interval", interval, fireAt: due);
        _definitionStore.Save(definition);
        var invocationCount = _sessionPipeline.InvocationCount;

        _timeProvider.Advance(interval);
        var controlProbe = CreateTestProbe("stale-interval-control");
        controlProbe.Send(manager, CreateOccurrenceEnvelope(definition, due, due + interval));
        controlProbe.Send(manager, GetReminderHealthQuery.Instance);
        var health = await controlProbe.ExpectMsgAsync<ReminderHealthResponse>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(invocationCount, _sessionPipeline.InvocationCount);
        Assert.Equal(1, await GetSkippedOccurrenceCountAsync(manager, definition.Id));
    }

    // Akka.Reminders retries this occurrence when the acknowledgement lease
    // ends, because the deadline is earlier than the next due time. A one-hour
    // attempt that starts with 60 minutes of lease can then run two times.
    [Fact]
    public async Task Interval_occurrence_with_short_acknowledgement_lease_is_skipped()
    {
        var manager = await GetManagerAsync();
        var due = _timeProvider.GetUtcNow();
        var definition = CreateIntervalDefinition("short-lease-interval", TimeSpan.FromHours(2), fireAt: due);
        _definitionStore.Save(definition);
        var invocationCount = _sessionPipeline.InvocationCount;

        var controlProbe = CreateTestProbe("short-lease-interval-control");
        controlProbe.Send(manager, CreateOccurrenceEnvelope(definition, due, due.AddMinutes(60)));
        controlProbe.Send(manager, GetReminderHealthQuery.Instance);
        var health = await controlProbe.ExpectMsgAsync<ReminderHealthResponse>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(invocationCount, _sessionPipeline.InvocationCount);
        Assert.Equal(1, await GetSkippedOccurrenceCountAsync(manager, definition.Id));
    }

    // The run of a daily reminder fails late and Akka.Reminders retries it ten
    // minutes before the next due time. A one-hour attempt would then block the
    // next occurrence, so the lease rule continues to skip this retry.
    [Fact]
    public async Task Late_retry_of_long_interval_occurrence_is_skipped()
    {
        var manager = await GetManagerAsync();
        var interval = TimeSpan.FromHours(24);
        var due = _timeProvider.GetUtcNow();
        var definition = CreateIntervalDefinition("late-daily-retry", interval, fireAt: due);
        _definitionStore.Save(definition);
        var invocationCount = _sessionPipeline.InvocationCount;

        _timeProvider.Advance(interval - TimeSpan.FromMinutes(10));
        var controlProbe = CreateTestProbe("late-daily-retry-control");
        controlProbe.Send(manager, CreateOccurrenceEnvelope(definition, due, due + interval));
        controlProbe.Send(manager, GetReminderHealthQuery.Instance);
        var health = await controlProbe.ExpectMsgAsync<ReminderHealthResponse>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(invocationCount, _sessionPipeline.InvocationCount);
        Assert.Equal(1, await GetSkippedOccurrenceCountAsync(manager, definition.Id));
    }

    [Fact]
    public async Task Cron_occurrence_with_short_acknowledgement_lease_is_skipped()
    {
        var manager = await GetManagerAsync();
        var due = _timeProvider.GetUtcNow();
        var definition = CreateIntervalDefinition("short-lease-cron", TimeSpan.FromMinutes(5), fireAt: due) with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Cron,
                CronExpression = "*/5 * * * *"
            }
        };
        _definitionStore.Save(definition);
        var invocationCount = _sessionPipeline.InvocationCount;

        var controlProbe = CreateTestProbe("short-lease-cron-control");
        controlProbe.Send(manager, CreateOccurrenceEnvelope(definition, due, due.AddMinutes(60)));
        controlProbe.Send(manager, GetReminderHealthQuery.Instance);
        var health = await controlProbe.ExpectMsgAsync<ReminderHealthResponse>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(invocationCount, _sessionPipeline.InvocationCount);
        Assert.Equal(1, await GetSkippedOccurrenceCountAsync(manager, definition.Id));
    }

    private static async Task<int> GetSkippedOccurrenceCountAsync(IActorRef manager, ReminderId id)
    {
        var status = await manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(id, OperatorAuthorization),
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        return status.SkippedDuplicates;
    }

    private static ReminderDefinition CreateIntervalDefinition(string id, TimeSpan interval, DateTimeOffset fireAt)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = id,
            Instructions = "Check status",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                Interval = interval,
                FireAt = fireAt
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ReminderEnvelope<ReminderPayload> CreateOccurrenceEnvelope(
        ReminderDefinition definition,
        DateTimeOffset due,
        DateTimeOffset deadline) =>
        new(
            new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new ReminderKey(definition.Id.Value),
            due,
            new ReminderDeadline(deadline),
            new ReminderPayload { Id = definition.Id });

    /// <summary>
    /// Test-only gateway stub: handles <see cref="DeliverTrustedSessionTurn"/>
    /// by forwarding to a probe for assertions and immediately replying
    /// <see cref="CommandAck"/> to <c>Sender</c>. Simulates the end of the
    /// real Slack / SignalR routing chain where the leaf binding/session
    /// has accepted the turn and <c>TryReplyAck</c> has fired.
    /// </summary>
    private sealed class AutoAckTrustedGateway : ReceiveActor
    {
        public AutoAckTrustedGateway(IActorRef probe)
        {
            Receive<DeliverTrustedSessionTurn>(msg =>
            {
                probe.Tell(msg);
                Sender.Tell(CommandAck.For(msg.SessionId));
            });
        }
    }

    /// <summary>
    /// Test-only gateway stub: refuses every <see cref="DeliverTrustedSessionTurn"/> with a
    /// <see cref="CommandNack"/>, the way a session that cannot admit the fire does.
    /// </summary>
    private sealed class NackTrustedGateway : ReceiveActor
    {
        public NackTrustedGateway(string reason)
        {
            Receive<DeliverTrustedSessionTurn>(msg => Sender.Tell(CommandNack.For(msg.SessionId, reason)));
        }
    }

    internal sealed class FailingReminderSessionPipeline(string reason) : ISessionPipeline
    {
        private int _invocationCount;
        private volatile SessionHold? _hold;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        /// <summary>
        /// Holds each <see cref="CreateAsync"/> call for a session id that starts
        /// with <paramref name="sessionIdPrefix"/> until <see cref="ReleaseHeldSessions"/>.
        /// A held call keeps its reminder execution in flight. Without the hold,
        /// the call fails at once, and the manager retries the occurrence every
        /// 25 ms and disables the reminder at the fifth failure. A test that reads
        /// the state of a running occurrence must hold it, or the read races that
        /// retry loop.
        /// </summary>
        public void HoldSessions(string sessionIdPrefix) =>
            _hold = new SessionHold(sessionIdPrefix);

        public void ReleaseHeldSessions() => _hold?.Gate.TrySetResult();

        public async Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            var hold = _hold;
            if (hold is not null && sessionId.Value.StartsWith(hold.SessionIdPrefix, StringComparison.Ordinal))
                await hold.Gate.Task.WaitAsync(cancellationToken);

            throw new InvalidOperationException(reason);
        }

        private sealed record SessionHold(string SessionIdPrefix)
        {
            public TaskCompletionSource Gate { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(
            IWithSessionId feedback,
            CancellationToken ct = default) =>
            Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }

    /// <summary>
    /// Accepts <see cref="DeliverTrustedSessionTurn"/> messages and forwards them
    /// to a probe, but never replies to <c>Sender</c>. Keeps the execution actor's
    /// gateway Ask pending indefinitely so the reminder stays in _activeExecutions.
    /// </summary>
    private sealed class NeverReplyGateway : ReceiveActor
    {
        public NeverReplyGateway(IActorRef probe)
        {
            Receive<DeliverTrustedSessionTurn>(msg => probe.Tell(msg));
        }
    }

    // ── Scheduling-failure surfacing (Tier 1 hardening) ──
    //
    // A syntactically valid cron that never occurs (Feb 30) drives a deterministic
    // scheduling failure through the "no future occurrence" branch — no host, clock,
    // or timezone dependency. Definitions are written straight to the store to
    // simulate a persisted reminder whose schedule became unschedulable, then
    // reconcile is asked to restore it.

    private static async Task DrainStartupReconcileAsync(IActorRef manager)
    {
        // ActorOf can return before PreStart queues its reconcile.
        // Two ordered barriers drain both possible startup orderings.
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reconcile_surfaces_scheduling_failure_and_counts_it()
    {
        var manager = await GetManagerAsync();

        await DrainStartupReconcileAsync(manager);

        var definition = CreateCronDefinition("sched-fail", "0 0 30 2 *");
        _definitionStore.Save(definition);

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var after = _definitionStore.Get(definition.Id);
        Assert.NotNull(after);
        Assert.Equal(1, after!.ConsecutiveFailures);
        Assert.True(after.Enabled); // one failure is well below the threshold
        Assert.Contains(_notificationSink.Alerts, a =>
            a.Category == AlertType.ReminderScheduleFailed && a.Source == definition.Id.Value);
    }

    [Fact]
    public async Task Consecutive_scheduling_failures_auto_disable_and_alert_critical()
    {
        var manager = await GetManagerAsync();

        await DrainStartupReconcileAsync(manager);

        // One below threshold; the next scheduling failure crosses it.
        var definition = CreateCronDefinition(
            "sched-disable", "0 0 30 2 *",
            consecutiveFailures: ReminderManagerActor.FailurePauseThreshold - 1);
        _definitionStore.Save(definition);

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var after = _definitionStore.Get(definition.Id);
        Assert.NotNull(after);
        Assert.Equal(ReminderManagerActor.FailurePauseThreshold, after!.ConsecutiveFailures);
        Assert.False(after.Enabled);
        Assert.Equal(ReminderTerminalOutcome.Failed, after.TerminalOutcome);
        Assert.Contains(_notificationSink.Alerts, a =>
            a.Category == AlertType.ReminderAutoDisabled
            && a.Source == definition.Id.Value
            && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task Scheduling_failure_installs_no_timer()
    {
        // Anti-pattern guard: a reminder that cannot compute an occurrence must
        // install no schedule. It never silently falls back to a bogus fire time.
        var manager = await GetManagerAsync();

        await DrainStartupReconcileAsync(manager);

        var definition = CreateCronDefinition("sched-none", "0 0 30 2 *");
        _definitionStore.Save(definition);

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var status = await manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(definition.Id, OperatorAuthorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(status.Found);
        Assert.Null(status.NextFire); // no timer installed
        Assert.True(status.ConsecutiveFailures >= 1);
    }

    [Fact]
    public async Task Health_failed_count_includes_scheduling_failures()
    {
        var manager = await GetManagerAsync();

        await DrainStartupReconcileAsync(manager);

        _definitionStore.Save(CreateCronDefinition("sched-health", "0 0 30 2 *"));

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, health.FailedCount);
    }

    private static ReminderDefinition CreateCronDefinition(
        string name, string cron, int consecutiveFailures = 0)
    {
        var id = new ReminderId($"{name}-{Guid.NewGuid():N}"[..20]);
        var now = TimeProvider.System.GetUtcNow();

        return new ReminderDefinition
        {
            Id = id,
            Title = name,
            Instructions = "Cron scheduling-failure test",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.Channel, Transport = "slack", Address = "#general" },
            DeliveryInstructions = "Reply in-thread with concise status.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Cron,
                CronExpression = cron
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            ConsecutiveFailures = consecutiveFailures,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ReminderDefinition CreateDefinition(string name, string instructions)
    {
        var id = new ReminderId($"{name}-{Guid.NewGuid():N}"[..20]);
        var now = TimeProvider.System.GetUtcNow();

        return new ReminderDefinition
        {
            Id = id,
            Title = name,
            Instructions = instructions,
            Delivery = new ReminderDelivery { Kind = DeliveryKind.Channel, Transport = "slack", Address = "#general" },
            DeliveryInstructions = "Reply in-thread with concise status.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ReminderDefinition CreateCurrentSessionDefinition(
        string id,
        bool deliveryRequired,
        ChannelType originChannelType = ChannelType.Slack,
        string? sessionId = null)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = id,
            Instructions = "Check status",
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = sessionId ?? "C0123ABC/1712000000.000001",
                OriginChannelType = originChannelType
            },
            DeliveryRequired = deliveryRequired,
            DeliveryInstructions = "Reply in this session with the result.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddMinutes(5)
            },
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // A one-shot fire runs only the occurrence of the current definition, so the due time of its envelope
    // must equal the fire time of the definition.
    private static ReminderEnvelope<ReminderPayload> CreateEnvelope(ReminderDefinition definition)
        => CreateEnvelope(
            definition.Id.Value,
            definition.Schedule.Type == ReminderScheduleType.OneShot ? definition.Schedule.FireAt : null);

    private static ReminderEnvelope<ReminderPayload> CreateEnvelope(string reminderId, DateTimeOffset? dueTime = null)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderEnvelope<ReminderPayload>(
            entity: new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            key: new ReminderKey(reminderId),
            dueTimeUtc: dueTime ?? now,
            deadline: ReminderDeadline.Infinite,
            message: new ReminderPayload { Id = new ReminderId(reminderId) });
    }

    internal sealed class TestNotificationSink : IOperationalNotificationSink
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
