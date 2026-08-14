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
public class ReminderManagerActorTests : TestKit
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-tests-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly TestShardRegionResolver _sharedResolver = new();
    private ReminderDefinitionStore _definitionStore = null!;
    private TestNotificationSink _notificationSink = null!;
    private readonly FailingReminderSessionPipeline _sessionPipeline =
        new("persistence recovery failed");

    public ReminderManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSerializationVerification();

        var paths = new NetclawPaths(_basePath);
        paths.EnsureDirectoriesExist();
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
            new ListRemindersCommand(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(list.Reminders);
        Assert.Equal("test-list", list.Reminders[0].Title);
        Assert.Equal("Check status", list.Reminders[0].Instructions);
    }

    [Fact]
    public async Task Cancel_existing_reminder_disables_and_preserves_definition()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-cancel", "Check it");
        var authorization = new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test");

        await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, Authorization: authorization), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var cancelled = await manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(definition.Id), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(cancelled.Found);

        var preserved = _definitionStore.Get(definition.Id);
        Assert.NotNull(preserved);
        Assert.False(preserved!.Enabled);
    }

    [Fact]
    public async Task Cancel_nonexistent_returns_not_found()
    {
        var manager = await GetManagerAsync();

        var cancelled = await manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(new ReminderId("does-not-exist")),
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
            new GetReminderStatusQuery(definition.Id), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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
            new GetReminderStatusQuery(new ReminderId("does-not-exist")),
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
            new GetReminderStatusQuery(definition.Id),
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

        var paths = new NetclawPaths(_basePath);
        var restarted = Sys.ActorOf(
            CreateManagerProps(
                new ReminderDefinitionStore(paths),
                new ReminderHistoryStore(paths)),
            "reminder-manager-restarted");
        ActorRegistry.For(Sys).Register<ReminderManagerActorKey>(restarted, overwrite: true);
        _sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, restarted);

        var status = await restarted.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(definition.Id),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(status.Found);
        Assert.True(status.Enabled);
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.NotNull(status.NextFire);
        Assert.Equal("Pending", status.Occurrence?.CompletionStatus);
    }

    [Fact]
    public async Task Reconcile_deletes_completed_oneshot_and_retains_ambiguous_or_failed_oneshots()
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
        var historyStore = new ReminderHistoryStore(new NetclawPaths(_basePath));
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
        Assert.Null(_definitionStore.Get(completed.Id));
        Assert.Empty(await historyStore.ReadAsync(completed.Id, 10));
        Assert.NotNull(_definitionStore.Get(failed.Id));
        Assert.Single(await historyStore.ReadAsync(failed.Id, 10));
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
    public async Task Save_rejects_expiration_for_oneshot_reminders()
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

        Assert.False(response.Success);
        Assert.Equal(ReminderSaveError.Validation, response.Error);
        Assert.Contains("one-shot", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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
            paths);
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
            dueTimeUtc: now,
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
            dueTimeUtc: now,
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

    [Fact]
    public async Task CurrentSession_delivery_required_fails_when_delivery_is_not_observed()
    {
        var manager = await GetManagerAsync();

        var originalTimeout = ReminderExecutionActor.DeliveryObservedTimeout;
        ReminderExecutionActor.DeliveryObservedTimeout = TimeSpan.FromMilliseconds(250);
        try
        {
            var gatewayProbe = CreateTestProbe("current-session-timeout-gateway");
            var autoAckRef = Sys.ActorOf(
                Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
                "auto-ack-current-session-timeout");
            ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

            var definition = CreateCurrentSessionDefinition("current-session-timeout", deliveryRequired: true);
            _definitionStore.Save(definition);

            manager.Tell(CreateEnvelope(definition.Id.Value));

            var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(delivered.Source.ReminderId);

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(0, health.ActiveExecutions);
                Assert.Contains(_notificationSink.Alerts, alert =>
                    alert.Category == AlertType.ReminderExecutionFailed
                    && alert.Source == definition.Id.Value
                    && alert.Summary.Contains("delivery not observed", StringComparison.OrdinalIgnoreCase));
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
    }

    [Fact]
    public async Task CurrentSession_delivery_required_fails_fast_on_explicit_delivery_failure()
    {
        var manager = await GetManagerAsync();

        // Generous backstop: if the explicit failure signal weren't honored,
        // this test would hang for the full timeout rather than fail fast.
        var originalTimeout = ReminderExecutionActor.DeliveryObservedTimeout;
        ReminderExecutionActor.DeliveryObservedTimeout = TimeSpan.FromSeconds(30);
        try
        {
            var gatewayProbe = CreateTestProbe("current-session-failed-gateway");
            var autoAckRef = Sys.ActorOf(
                Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
                "auto-ack-current-session-failed");
            ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

            var definition = CreateCurrentSessionDefinition("current-session-failed", deliveryRequired: true);
            _definitionStore.Save(definition);

            manager.Tell(CreateEnvelope(definition.Id.Value));

            var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(delivered.Source.ReminderId);
            Assert.NotNull(delivered.Source.DeliveryObserver);

            // Channel reports the post failed — execution must report failure
            // (so Akka.Reminders redelivers) without acking the envelope.
            delivered.Source.DeliveryObserver!.Tell(new ReminderDeliveryResult(
                delivered.Source.ReminderId!.Value,
                ChannelType.Slack,
                Delivered: false,
                FailureReason: "channel API down"));

            await AwaitAssertAsync(() =>
            {
                Assert.Contains(_notificationSink.Alerts, alert =>
                    alert.Category == AlertType.ReminderExecutionFailed
                    && alert.Source == definition.Id.Value
                    && alert.Summary.Contains("channel API down", StringComparison.OrdinalIgnoreCase));
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
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

        var definition = CreateCurrentSessionDefinition("stable-key", deliveryRequired: true);
        _definitionStore.Save(definition);

        // A distinctive fire time far from "now": a key built from the dispatch
        // wall-clock would not match it.
        var fireTime = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
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

    [Fact]
    public async Task CurrentSession_delivery_required_succeeds_when_delivery_is_observed()
    {
        var manager = await GetManagerAsync();

        var originalTimeout = ReminderExecutionActor.DeliveryObservedTimeout;
        ReminderExecutionActor.DeliveryObservedTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var gatewayProbe = CreateTestProbe("current-session-observed-gateway");
            var autoAckRef = Sys.ActorOf(
                Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
                "auto-ack-current-session-observed");
            ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

            var definition = CreateCurrentSessionDefinition("current-session-observed", deliveryRequired: true);
            _definitionStore.Save(definition);

            manager.Tell(CreateEnvelope(definition.Id.Value));

            var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(delivered.Source.ReminderId);
            Assert.NotNull(delivered.Source.DeliveryObserver);

            delivered.Source.DeliveryObserver!.Tell(new ReminderDeliveryResult(
                delivered.Source.ReminderId!.Value,
                ChannelType.Slack,
                Delivered: true,
                ObservedAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()));

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(0, health.ActiveExecutions);
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
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

        var gatewayProbe = CreateTestProbe("capacity-gateway");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-capacity");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        // Fill three in-flight executions (the historical capacity limit).
        // Save before dispatch so filesystem latency cannot consume any test
        // timing window while execution slots are being filled.
        for (var i = 0; i < 3; i++)
        {
            var id = $"blocking-{i}";
            _definitionStore.Save(CreateCurrentSessionDefinition(id, deliveryRequired: true));
        }

        for (var i = 0; i < 3; i++)
            manager.Tell(CreateEnvelope($"blocking-{i}"));

        for (var i = 0; i < 3; i++)
        {
            var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(delivered.Source.ReminderId);
            Assert.NotNull(delivered.Source.DeliveryObserver);
        }

        var invocationCount = _sessionPipeline.InvocationCount;
        var now = TimeProvider.System.GetUtcNow();
        var recurringId = "concurrent-recurring";
        var recurringReminder = new ReminderDefinition
        {
            Id = new ReminderId(recurringId),
            Title = "Concurrent recurring reminder",
            Instructions = "Must start even while other reminders are executing",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                // Must exceed the 1h execution timeout + settlement margin,
                // otherwise the safe-execution-lease check skips the occurrence.
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
                new GetReminderStatusQuery(recurringReminder.Id),
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
            // The historical capacity gate would have skipped this occurrence
            // (SkippedDuplicates == 1) without starting an execution. With the
            // cap removed the occurrence must be dispatched, not skipped.
            Assert.Equal(0, status.SkippedDuplicates);
            Assert.True(_sessionPipeline.InvocationCount > invocationCount,
                "Expected the recurring reminder to be executed by the pipeline.");
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var stored = _definitionStore.Get(recurringReminder.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.Enabled);
    }

    [Fact]
    public async Task CurrentSession_discord_delivery_required_fails_when_delivery_is_not_observed()
    {
        var manager = await GetManagerAsync();

        var originalTimeout = ReminderExecutionActor.DeliveryObservedTimeout;
        ReminderExecutionActor.DeliveryObservedTimeout = TimeSpan.FromMilliseconds(250);
        try
        {
            var gatewayProbe = CreateTestProbe("discord-current-session-timeout-gateway");
            var autoAckRef = Sys.ActorOf(
                Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
                "auto-ack-discord-current-session-timeout");
            ActorRegistry.For(Sys).Register<DiscordGatewayActorKey>(autoAckRef);

            var definition = CreateCurrentSessionDefinition(
                "discord-current-session-timeout",
                deliveryRequired: true,
                originChannelType: ChannelType.Discord,
                sessionId: "129847561203948576/130111223344556677");
            _definitionStore.Save(definition);

            manager.Tell(CreateEnvelope(definition.Id.Value));

            var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(delivered.Source.ReminderId);

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(0, health.ActiveExecutions);
                Assert.Contains(_notificationSink.Alerts, alert =>
                    alert.Category == AlertType.ReminderExecutionFailed
                    && alert.Source == definition.Id.Value
                    && alert.Summary.Contains("delivery not observed", StringComparison.OrdinalIgnoreCase));
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
    }

    [Fact]
    public async Task CurrentSession_discord_delivery_required_succeeds_when_delivery_is_observed()
    {
        var manager = await GetManagerAsync();

        var originalTimeout = ReminderExecutionActor.DeliveryObservedTimeout;
        ReminderExecutionActor.DeliveryObservedTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var gatewayProbe = CreateTestProbe("discord-current-session-observed-gateway");
            var autoAckRef = Sys.ActorOf(
                Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
                "auto-ack-discord-current-session-observed");
            ActorRegistry.For(Sys).Register<DiscordGatewayActorKey>(autoAckRef);

            var definition = CreateCurrentSessionDefinition(
                "discord-current-session-observed",
                deliveryRequired: true,
                originChannelType: ChannelType.Discord,
                sessionId: "129847561203948576/130111223344556677");
            _definitionStore.Save(definition);

            manager.Tell(CreateEnvelope(definition.Id.Value));

            var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(delivered.Source.ReminderId);
            Assert.NotNull(delivered.Source.DeliveryObserver);

            delivered.Source.DeliveryObserver!.Tell(new ReminderDeliveryResult(
                delivered.Source.ReminderId!.Value,
                ChannelType.Discord,
                Delivered: true,
                ObservedAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()));

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(0, health.ActiveExecutions);
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
    }

    [Fact]
    public async Task Channel_failure_retries_and_fifth_failure_disables_reminder()
    {
        var manager = await GetManagerAsync();
        var definition = CreateDefinition("retry-poison", "Run the briefing") with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = _timeProvider.GetUtcNow().AddMilliseconds(100)
            }
        };

        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);

        await AwaitAssertAsync(() =>
        {
            var stored = _definitionStore.Get(definition.Id);
            Assert.NotNull(stored);
            Assert.False(stored!.Enabled);
            Assert.Equal(ReminderManagerActor.FailurePauseThreshold, stored.ConsecutiveFailures);
            Assert.Equal(ReminderTerminalOutcome.Failed, stored.TerminalOutcome);
            Assert.Equal(ReminderManagerActor.FailurePauseThreshold, _sessionPipeline.InvocationCount);
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Successful_retry_deletes_oneshot_definition_and_history()
    {
        var manager = await GetManagerAsync();
        var gatewayProbe = CreateTestProbe("retry-success-gateway");
        var gateway = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-retry-success-gateway");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(gateway);

        var definition = CreateCurrentSessionDefinition("retry-success", deliveryRequired: false) with
        {
            ConsecutiveFailures = 3,
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = _timeProvider.GetUtcNow().AddMilliseconds(100)
            }
        };

        var saved = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(
                definition,
                Authorization: new ReminderAudienceAuthorizationContext(TrustAudience.Team, "test")),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(saved.Success, saved.ErrorMessage);

        await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        await AwaitAssertAsync(async () =>
        {
            Assert.Null(_definitionStore.Get(definition.Id));
            var historyStore = new ReminderHistoryStore(new NetclawPaths(_basePath));
            Assert.Empty(await historyStore.ReadAsync(definition.Id, 10));
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
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

        var envelope = CreateEnvelope(definition.Id.Value);

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

    private sealed class FailingReminderSessionPipeline(string reason) : ISessionPipeline
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            throw new InvalidOperationException(reason);
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

    [Fact]
    public async Task Reconcile_surfaces_scheduling_failure_and_counts_it()
    {
        var manager = await GetManagerAsync();

        // Drain PreStart's reconcile (it ran against an empty store) so the write
        // below is bumped exactly once by our explicit reconcile.
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var definition = CreateCronDefinition("sched-none", "0 0 30 2 *");
        _definitionStore.Save(definition);

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var status = await manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(definition.Id), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(status.Found);
        Assert.Null(status.NextFire); // no timer installed
        Assert.True(status.ConsecutiveFailures >= 1);
    }

    [Fact]
    public async Task Health_failed_count_includes_scheduling_failures()
    {
        var manager = await GetManagerAsync();

        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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

    private static ReminderEnvelope<ReminderPayload> CreateEnvelope(string reminderId)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderEnvelope<ReminderPayload>(
            entity: new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            key: new ReminderKey(reminderId),
            dueTimeUtc: now,
            deadline: ReminderDeadline.Infinite,
            message: new ReminderPayload { Id = new ReminderId(reminderId) });
    }

    private sealed class TestNotificationSink : IOperationalNotificationSink
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
