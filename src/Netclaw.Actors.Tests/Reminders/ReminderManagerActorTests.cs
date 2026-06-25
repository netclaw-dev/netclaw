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
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Hosting;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

public class ReminderManagerActorTests : TestKit
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-tests-{Guid.NewGuid():N}");
    private ReminderDefinitionStore _definitionStore = null!;
    private TestNotificationSink _notificationSink = null!;

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
        var sharedResolver = new TestShardRegionResolver();
        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => sharedResolver);
        });

        builder.StartActors((system, registry, _) =>
        {
            // Create a minimal SessionPipeline stub — manager needs it but
            // we won't actually execute reminders in these tests.
            registry.Register<SessionManagerActorKey>(system.DeadLetters);

            var pipeline = new SessionPipeline(
                system,
                new RequiredActor<SessionManagerActorKey>(ActorRegistry.For(system)),
                new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}")));

            var defaults = new EffectivePolicyDefaults(
                DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);
            var reminderManager = system.ActorOf(
                Props.Create(() => new ReminderManagerActor(
                    pipeline,
                    defaults,
                    new SchedulingConfig(),
                    TimeProvider.System,
                    definitionStore,
                    historyStore,
                    _notificationSink)),
                "reminder-manager-test");

            registry.Register<ReminderManagerActorKey>(reminderManager);
            sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, reminderManager);
        });
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
    public async Task Reconcile_deletes_zombie_oneshot_reminders()
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

        // Confirm it shows up as scheduled
        var healthBefore = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, healthBefore.ScheduledCount);

        // Trigger reconciliation and wait for completion ack
        var reconcileResult = await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, reconcileResult.DeletedOneShots);

        // Verify definition has been deleted from the store
        var afterReconcile = _definitionStore.Get(new ReminderId("zombie-oneshot"));
        Assert.Null(afterReconcile);
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
        const string reminderId = "legacy-reminder-alert";
        var now = TimeProvider.System.GetUtcNow();
        var paths = new NetclawPaths(_basePath);
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
            new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}")));
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
                sink)),
            "legacy-reminder-alert-manager");

        // The legacy-schema alert is emitted synchronously inside PreStart, and
        // an actor processes mailbox messages only AFTER PreStart completes — so
        // a successful health reply is a deterministic signal that PreStart (and
        // the emit) has run. Awaiting that signal replaces a wall-clock
        // AwaitAssertAsync(5s) poll that flaked under heavy parallel CI load: when
        // the shared ThreadPool is saturated, PreStart can be scheduled later than
        // a fixed 5s budget, leaving the sink empty when the poll gives up. The
        // generous Ask timeout absorbs that scheduling latency without polling —
        // it returns as soon as the actor is ready.
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
        Assert.StartsWith("mode-b-anchor:", delivered.Source.ReminderId);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, delivered.Source.Principal);
        Assert.Equal("reminder", delivered.Source.Provenance.SourceKind?.Value);

        // Probe receiving the DeliverTrustedSessionTurn is the anchor
        // assertion: it proves the manager branched on OriginChannelType,
        // spawned the execution actor with the envelope, resolved the
        // gateway via ActorRegistry, and issued the Ask. The probe's
        // CommandAck reply + the subsequent _client.AckAsync path is
        // exercised end-to-end here too (execution actor didn't crash
        // or report a failure up to the manager's _failureCounts); any
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
                delivered.Source.ReminderId!,
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
        Assert.Equal($"{definition.Id}:{fireTime.ToUnixTimeMilliseconds()}", delivered.Source.ReminderId);
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
                delivered.Source.ReminderId!,
                ChannelType.Slack,
                Delivered: true,
                ObservedAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()));

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(0, health.FailedCount);
                Assert.DoesNotContain(_notificationSink.Alerts, alert =>
                    alert.Category == AlertType.ReminderExecutionFailed
                    && alert.Source == definition.Id.Value);
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
        var now = TimeProvider.System.GetUtcNow();

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
    public async Task Deferred_expired_recurring_reminder_is_disabled_before_execution()
    {
        var manager = await GetManagerAsync();

        // Drain PreStart reconcile
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var originalTimeout = ReminderExecutionActor.DeliveryObservedTimeout;
        ReminderExecutionActor.DeliveryObservedTimeout = TimeSpan.FromMilliseconds(700);
        try
        {
            var gatewayProbe = CreateTestProbe("deferred-expiry-gateway");
            var autoAckRef = Sys.ActorOf(
                Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
                "auto-ack-deferred-expiry");
            ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

            // Saturate execution slots with CurrentSession reminders waiting for
            // delivery observation timeout.
            for (var i = 0; i < ReminderManagerActor.MaxConcurrentExecutions; i++)
            {
                var id = $"blocking-{i}";
                _definitionStore.Save(CreateCurrentSessionDefinition(id, deliveryRequired: true));
                manager.Tell(CreateEnvelope(id));
            }

            var now = TimeProvider.System.GetUtcNow();
            var expiringId = "queued-expiring";
            var expiringReminder = new ReminderDefinition
            {
                Id = new ReminderId(expiringId),
                Title = "Queued expiring reminder",
                Instructions = "Should not execute after expiry",
                Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
                Schedule = new ReminderSchedule
                {
                    Type = ReminderScheduleType.Interval,
                    Interval = TimeSpan.FromMinutes(30),
                    FireAt = now.AddMinutes(30)
                },
                ExpiresAt = now.AddMilliseconds(200),
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                Enabled = true,
                CreatedBy = "test",
                CreatedAt = now,
                UpdatedAt = now
            };
            _definitionStore.Save(expiringReminder);

            manager.Tell(CreateEnvelope(expiringId));

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(ReminderManagerActor.MaxConcurrentExecutions, health.ActiveExecutions);
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

            await AwaitAssertAsync(async () =>
            {
                var stored = _definitionStore.Get(new ReminderId(expiringId));
                Assert.NotNull(stored);
                Assert.False(stored!.Enabled);
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(_notificationSink.Alerts, alert =>
                alert.Category == AlertType.ReminderExecutionFailed
                && alert.Source == expiringId);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
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
                delivered.Source.ReminderId!,
                ChannelType.Discord,
                Delivered: true,
                ObservedAtMs: TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()));

            await AwaitAssertAsync(async () =>
            {
                var health = await manager.Ask<ReminderHealthResponse>(
                    GetReminderHealthQuery.Instance,
                    TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);

                Assert.Equal(0, health.FailedCount);
                Assert.DoesNotContain(_notificationSink.Alerts, alert =>
                    alert.Category == AlertType.ReminderExecutionFailed
                    && alert.Source == definition.Id.Value);
            }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            ReminderExecutionActor.DeliveryObservedTimeout = originalTimeout;
        }
    }

    [Fact]
    public async Task Second_fire_of_executing_reminder_is_skipped_as_duplicate()
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

        var envelope1 = CreateEnvelope(definition.Id.Value);
        var envelope2 = CreateEnvelope(definition.Id.Value);

        // Both envelopes go into the actor's mailbox before either is processed,
        // so the second always arrives while the first execution is still in flight.
        manager.Tell(envelope1);
        manager.Tell(envelope2);

        // Exactly one delivery should reach the gateway.
        await deliveryProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await deliveryProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        // Health reflects one active execution — the duplicate was dropped, not queued.
        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, health.ActiveExecutions);

        // No failure alert — duplicate skip is silent from an alert standpoint.
        Assert.DoesNotContain(_notificationSink.Alerts, a =>
            a.Category == AlertType.ReminderExecutionFailed && a.Source == definition.Id.Value);
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
