// -----------------------------------------------------------------------
// <copyright file="OneShotEndOutcomeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Every terminal event of a one-shot reminder ends it: Netclaw writes the tombstone, acknowledges the
/// occurrence, and deletes the schedule and the definition (change <c>oneshot-reminder-autodelete</c>,
/// design D2 and D3). A recurring reminder keeps its definition.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotEndOutcomeTests : OneShotReminderTestBase
{
    public OneShotEndOutcomeTests(ITestOutputHelper output) : base(output) { }

    private async Task<ReminderDefinition> RunOnceAsync(string id, RunMode mode)
    {
        Pipeline.Mode = mode;
        var definition = NewOneShot(id);
        await ScheduleAsync(definition);
        Advance(FirstFireDelay);
        await AwaitSettledRunAsync(definition.Id, 1);
        return definition;
    }

    private async Task AssertEndedAsync(ReminderDefinition definition, string outcome)
    {
        Assert.True(
            Definitions.Get(definition.Id) is null,
            $"The definition of '{definition.Id.Value}' still exists after the one-shot ended as {outcome}.");
        Assert.False(await IsScheduledAsync(definition.Id), "The Akka.Reminders schedule still exists.");
        Assert.Equal(outcome, OutcomeOf(RequireTombstone(definition.Id)));
    }

    // ── Success ──

    [Fact]
    public async Task Successful_oneshot_is_deleted_and_leaves_a_completed_tombstone_with_history()
    {
        await DrainStartupAsync();

        var definition = await RunOnceAsync("done-oneshot", RunMode.Succeed);

        await AssertEndedAsync(definition, "Completed");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal(definition.Id.Value, Text(tombstone, "id"));
        Assert.Equal(definition.Title, Text(tombstone, "title"));
        Assert.Equal("Team", Text(tombstone, "audience"));
        Assert.Equal(definition.Schedule.FireAtMs, Number(tombstone, "fireAtMs"));
        Assert.Equal(Clock.GetUtcNow().ToUnixTimeMilliseconds(), Number(tombstone, "endedAtMs"));
        Assert.Null(Text(tombstone, "lastError"));
        Assert.Equal("Delivered", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
        var history = await HistoryAsync(definition.Id, TeamCaller);
        Assert.True(history.Found);
        Assert.Equal("ok", Assert.Single(history.Records).Status);
    }

    [Fact]
    public async Task CurrentSession_oneshot_ends_at_admission_as_completed()
    {
        await DrainStartupAsync();
        var admitted = CreateTestProbe("admitted");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(
            Sys.ActorOf(Props.Create(() => new AckGateway(admitted.Ref)), "ack-gateway"));
        var definition = NewOneShot("session-oneshot") with
        {
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "C0123ABC/1712000000.000001",
                OriginChannelType = ChannelType.Slack
            },
            DeliveryRequired = false
        };
        await ScheduleAsync(definition);

        Advance(FirstFireDelay);
        await admitted.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            AskTimeout, cancellationToken: TestContext.Current.CancellationToken);
        await AwaitSettledRunAsync(definition.Id, 1);

        await AssertEndedAsync(definition, "Completed");
    }

    // ── Failure ──

    [Fact]
    public async Task Oneshot_failure_below_threshold_keeps_the_definition_enabled()
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Fail;
        var definition = NewOneShot("retrying-oneshot");
        await ScheduleAsync(definition);

        Advance(FirstFireDelay);
        await AwaitSettledRunAsync(definition.Id, 1);
        Advance(TimeSpan.FromSeconds(60));
        await AwaitSettledRunAsync(definition.Id, 2);

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(2, stored.ConsecutiveFailures);
        Assert.Null(stored.TerminalOutcome);
        Assert.False(TombstoneExists(definition.Id), "A failure below the threshold must not end the one-shot.");
        Assert.True(await IsScheduledAsync(definition.Id));
        var status = await StatusAsync(definition.Id, TeamCaller);
        Assert.Equal("Pending", status.Occurrence?.CompletionStatus);
        Assert.Equal(2, status.Occurrence?.AttemptCount);
    }

    [Fact]
    public async Task Oneshot_fifth_failure_ends_failed_and_cancels_the_schedule()
    {
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Fail;
        var definition = NewOneShot("poisoned-oneshot");
        await ScheduleAsync(definition);

        // Akka.Reminders retries a one-shot 60, 120, 240 and 480 seconds apart.
        var elapsedSeconds = -(int)FirstFireDelay.TotalSeconds;
        var attempt = 0;
        foreach (var offset in new[] { 0, 60, 180, 420, 900 })
        {
            Advance(TimeSpan.FromSeconds(offset - elapsedSeconds));
            elapsedSeconds = offset;
            await AwaitSettledRunAsync(definition.Id, ++attempt);
        }

        await AssertEndedAsync(definition, "Failed");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Contains(FailureText, Text(tombstone, "lastError"), StringComparison.Ordinal);
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        Assert.Single(Alerts.Alerts, a => a.Type == "reminder.auto_disabled" && a.Severity == AlertSeverity.Critical);
        var history = await HistoryAsync(definition.Id, TeamCaller);
        Assert.True(history.Found);
        Assert.Equal(5, history.Records.Count);
        Assert.All(history.Records, r => Assert.Equal("failed", r.Status));
    }

    // ── Denied tool call ──

    [Fact]
    public async Task Denied_oneshot_run_ends_failed_with_denied_history()
    {
        await DrainStartupAsync();

        var definition = await RunOnceAsync("denied-oneshot", RunMode.Deny);

        await AssertEndedAsync(definition, "Failed");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Contains(DeniedText, Text(tombstone, "lastError"), StringComparison.Ordinal);
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        var history = await HistoryAsync(definition.Id, TeamCaller);
        Assert.True(history.Found);
        Assert.Equal("denied", Assert.Single(history.Records).Status);
        // A denial is deterministic: Netclaw acknowledges the occurrence and does not retry it.
        Assert.Equal("Delivered", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
        Assert.Equal(1, Pipeline.InvocationCount);
        Assert.Single(Alerts.Alerts, a => a.Type == "reminder.tool_denied");
        Assert.DoesNotContain(Alerts.Alerts, a => a.Type == "reminder.auto_disabled");
    }

    // ── Expiry ──

    [Fact]
    public async Task Expired_oneshot_fire_ends_expired_without_executing()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("expiring-oneshot") with { ExpiresAt = Clock.GetUtcNow() + TimeSpan.FromSeconds(5) };
        await ScheduleAsync(definition);

        Advance(FirstFireDelay);
        await AwaitEnvelopesHandledAsync(1);

        await AssertEndedAsync(definition, "Expired");
        Assert.Equal(0, Pipeline.InvocationCount);
        Assert.Null(Text(RequireTombstone(definition.Id), "instructions"));
        Assert.Equal("Delivered", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
    }

    [Fact]
    public async Task Expired_recurring_fire_stays_disabled_and_writes_no_tombstone()
    {
        await DrainStartupAsync();
        var interval = TimeSpan.FromMinutes(20);
        var definition = NewInterval("expiring-interval", interval) with
        {
            ExpiresAt = Clock.GetUtcNow() + TimeSpan.FromSeconds(5)
        };
        await ScheduleAsync(definition);

        // A saved interval reminder first fires one interval after the save.
        Advance(interval);
        await AwaitEnvelopesHandledAsync(1);

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.False(stored.Enabled);
        Assert.Empty(TombstoneFiles());
        Assert.Equal(0, Pipeline.InvocationCount);
    }

    // ── Cancel ──

    [Fact]
    public async Task Cancel_oneshot_deletes_it_and_writes_a_cancelled_tombstone()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("cancel-oneshot");
        await ScheduleAsync(definition);

        var cancelled = await CancelAsync(definition.Id, TeamCaller);

        Assert.True(cancelled.Found);
        await AssertEndedAsync(definition, "Cancelled");
        Assert.Null(Text(RequireTombstone(definition.Id), "instructions"));
        Advance(FirstFireDelay);
        await BarrierAsync();
        Assert.Equal(0, Envelopes.Count);
        Assert.Equal(0, Pipeline.InvocationCount);
    }

    [Fact]
    public async Task Cancel_recurring_disables_and_keeps_it()
    {
        await DrainStartupAsync();
        var definition = NewInterval("cancel-interval", TimeSpan.FromMinutes(30));
        await ScheduleAsync(definition);

        var cancelled = await CancelAsync(definition.Id, TeamCaller);

        Assert.True(cancelled.Found);
        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.False(stored.Enabled);
        Assert.Empty(TombstoneFiles());
        Assert.False(await IsScheduledAsync(definition.Id));
    }

    // ── Tombstone content ──

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public async Task Tombstone_holds_the_prompt_only_when_the_outcome_is_failed(string outcome)
    {
        await DrainStartupAsync();
        var definition = await EndAsOutcomeAsync($"prompt-{outcome.ToLowerInvariant()}", outcome);

        var tombstone = RequireTombstone(definition.Id);
        var path = TombstonePath(definition.Id);
        Assert.Equal(outcome, OutcomeOf(tombstone));
        if (outcome == "Failed")
        {
            Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        }
        else
        {
            Assert.False(TryGet(tombstone, "instructions", out _), "Only a Failed tombstone may hold the prompt.");
            Assert.DoesNotContain(DefaultPrompt, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public async Task No_tombstone_holds_the_delivery_target(string outcome)
    {
        await DrainStartupAsync();
        var definition = await EndAsOutcomeAsync($"target-{outcome.ToLowerInvariant()}", outcome);

        var tombstone = RequireTombstone(definition.Id);
        var text = await File.ReadAllTextAsync(TombstonePath(definition.Id), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("#secret-room", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret delivery note", text, StringComparison.Ordinal);
        foreach (var name in new[] { "delivery", "boundary", "deliveryInstructions", "schedule" })
            Assert.False(TryGet(tombstone, name, out _), $"The tombstone must not hold '{name}'.");
    }

    private async Task<ReminderDefinition> EndAsOutcomeAsync(string id, string outcome)
    {
        switch (outcome)
        {
            case "Completed":
                return await RunOnceAsync(id, RunMode.Succeed);
            case "Failed":
                return await RunOnceAsync(id, RunMode.Deny);
            case "Cancelled":
                var cancelled = NewOneShot(id);
                await ScheduleAsync(cancelled);
                await CancelAsync(cancelled.Id, TeamCaller);
                return cancelled;
            default:
                var expiring = NewOneShot(id) with { ExpiresAt = Clock.GetUtcNow() + TimeSpan.FromSeconds(5) };
                await ScheduleAsync(expiring);
                Advance(FirstFireDelay);
                await AwaitEnvelopesHandledAsync(1);
                return expiring;
        }
    }

    [Fact]
    public async Task A_reminder_id_that_looks_like_a_path_writes_its_tombstone_inside_the_ended_folder()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("../../escape");
        await ScheduleAsync(definition);

        await CancelAsync(definition.Id, TeamCaller);

        Assert.Single(TombstoneFiles());
        // Only the definition and the history may sit next to the tombstone folder. Nothing may leave the reminders folder.
        var outside = Directory.EnumerateFiles(Paths.BasePath, "*escape*", SearchOption.AllDirectories)
            .Where(f => Path.GetDirectoryName(f) != Paths.RemindersDirectory && Path.GetDirectoryName(f) != EndedDirectory)
            .ToArray();
        Assert.Empty(outside);
    }

    // ── Crash window: the tombstone cannot be written ──

    // A run that succeeded or had a denied tool call must not repeat when the tombstone write fails. The
    // acknowledgement records the durable status Delivered, so Akka.Reminders does not deliver the occurrence
    // again. The definition stays, and the startup reconciliation ends it from the durable status.
    [Theory]
    [InlineData(RunMode.Succeed)]
    [InlineData(RunMode.Deny)]
    public async Task Oneshot_tombstone_write_failure_still_acks_and_never_runs_again(RunMode mode)
    {
        await DrainStartupAsync();
        Pipeline.Mode = mode;
        var definition = NewOneShot("blocked-oneshot");
        await ScheduleAsync(definition);
        // A folder at the temp-file path makes the atomic write fail, as in the other store failure tests.
        var blocker = Path.Combine(EndedDirectory, $"{Uri.EscapeDataString(definition.Id.Value)}.json.tmp");
        Directory.CreateDirectory(blocker);

        Advance(FirstFireDelay);
        await AwaitSettledRunAsync(definition.Id, 1);

        Assert.Equal("Delivered", await OccurrenceStatusAsync(definition.Id, definition.Schedule.FireAt!.Value));
        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled, "The definition must stay so that the reconciliation can end the one-shot.");
        Assert.False(TombstoneExists(definition.Id));
        Assert.Contains(Alerts.Alerts, a => a.Type == "reminder.settlement.failed" && a.Source == definition.Id.Value);
        Assert.NotEmpty(LogsOfLevel(Akka.Event.LogLevel.ErrorLevel, definition.Id.Value));

        // The acknowledgement lease is 70 minutes. Move past it: nothing may deliver the occurrence again.
        for (var step = 0; step < 9; step++)
            Advance(TimeSpan.FromMinutes(10));
        await BarrierAsync();
        Assert.Equal(1, Pipeline.InvocationCount);
        Assert.Equal(1, Envelopes.Count);

        // The disk works again. The restart finds the enabled definition with a past fire time and a
        // Delivered occurrence, and it ends the one-shot as Completed.
        Directory.Delete(blocker);
        await RestartManagerAsync();

        await AssertEndedAsync(definition, "Completed");
        Assert.Equal(1, Pipeline.InvocationCount);
        Assert.Single(await History.ReadAsync(definition.Id, 10));
    }

    [Fact]
    public async Task Cancel_with_a_tombstone_write_failure_keeps_the_schedule_and_the_definition()
    {
        await DrainStartupAsync();
        var definition = NewOneShot("blocked-cancel");
        await ScheduleAsync(definition);
        Directory.CreateDirectory(Path.Combine(EndedDirectory, $"{Uri.EscapeDataString(definition.Id.Value)}.json.tmp"));

        await CancelAsync(definition.Id, TeamCaller);

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled, "A cancel must not change the definition when the tombstone cannot be written.");
        Assert.True(await IsScheduledAsync(definition.Id));
        Assert.False(TombstoneExists(definition.Id));
        Assert.Contains(Alerts.Alerts, a => a.Type == "reminder.settlement.failed" && a.Source == definition.Id.Value);
    }

    // ── Scheduling failure ──

    // The restore at startup is the only place that can fail to schedule a one-shot. The test removes the
    // shard region, so Akka.Reminders replies ShardRegionNotFound to the schedule request.
    private ReminderDefinition NewUnschedulableOneShot(string id, int priorFailures)
        => NewOneShot(id, fireIn: TimeSpan.FromHours(1)) with { ConsecutiveFailures = priorFailures };

    [Fact]
    public async Task Oneshot_that_reaches_the_scheduling_failure_threshold_ends_failed()
    {
        await DrainStartupAsync();
        var definition = NewUnschedulableOneShot("unschedulable", ReminderManagerActor.FailurePauseThreshold - 1);
        Definitions.Save(definition);
        MakeShardRegionUnavailable();

        await ReconcileAsync();

        await AssertEndedAsync(definition, "Failed");
        var tombstone = RequireTombstone(definition.Id);
        Assert.Contains("could not be scheduled", Text(tombstone, "lastError"), StringComparison.Ordinal);
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        Assert.Single(Alerts.Alerts, a => a.Type == "reminder.schedule.failed" && a.Source == definition.Id.Value);
        Assert.Single(Alerts.Alerts, a =>
            a.Type == "reminder.auto_disabled" && a.Severity == AlertSeverity.Critical && a.Source == definition.Id.Value);
    }

    [Fact]
    public async Task Oneshot_scheduling_failure_below_the_threshold_keeps_the_definition_and_counts_it()
    {
        await DrainStartupAsync();
        var definition = NewUnschedulableOneShot("unschedulable-once", priorFailures: 0);
        Definitions.Save(definition);
        MakeShardRegionUnavailable();

        await ReconcileAsync();

        var stored = Definitions.Get(definition.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(1, stored.ConsecutiveFailures);
        Assert.False(TombstoneExists(definition.Id));
        Assert.Single(Alerts.Alerts, a => a.Type == "reminder.schedule.failed" && a.Source == definition.Id.Value);
        Assert.DoesNotContain(Alerts.Alerts, a => a.Type == "reminder.auto_disabled");
    }

    [Fact]
    public async Task Oneshot_scheduling_failure_at_the_threshold_with_a_tombstone_write_failure_still_alerts()
    {
        await DrainStartupAsync();
        var definition = NewUnschedulableOneShot("unschedulable-blocked", ReminderManagerActor.FailurePauseThreshold - 1);
        Definitions.Save(definition);
        MakeShardRegionUnavailable();
        Directory.CreateDirectory(Path.Combine(EndedDirectory, $"{Uri.EscapeDataString(definition.Id.Value)}.json.tmp"));

        await ReconcileAsync();

        // The end stopped at the tombstone: the definition stays for the next attempt. The operator must
        // still learn why the reminder has no schedule.
        Assert.NotNull(Definitions.Get(definition.Id));
        Assert.False(TombstoneExists(definition.Id));
        Assert.Contains(Alerts.Alerts, a => a.Type == "reminder.settlement.failed" && a.Source == definition.Id.Value);
        Assert.Contains(
            Alerts.Alerts,
            a => a.Type == "reminder.schedule.failed" && a.Source == definition.Id.Value);
        // The definition is not disabled, so no alert may say that it is. The next attempt ends it.
        Assert.DoesNotContain(Alerts.Alerts, a => a.Type == "reminder.auto_disabled");
    }

    private sealed class AckGateway : ReceiveActor
    {
        public AckGateway(IActorRef probe)
        {
            Receive<DeliverTrustedSessionTurn>(turn =>
            {
                probe.Tell(turn);
                Sender.Tell(CommandAck.For(turn.SessionId));
            });
        }
    }
}

/// <summary>
/// Akka.Reminders replies <c>Failed</c> to a negative acknowledgement when it has no attempt left.
/// The one-shot then ends as <c>Failed</c>, even below Netclaw's own failure threshold.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public sealed class OneShotRetriesExhaustedTests : OneShotReminderTestBase
{
    public OneShotRetriesExhaustedTests(ITestOutputHelper output) : base(output) { }

    protected override int MaxDeliveryAttempts => 3;

    [Fact]
    public async Task Oneshot_ends_failed_when_akka_reminders_runs_out_of_attempts()
    {
        Assert.True(MaxDeliveryAttempts < ReminderManagerActor.FailurePauseThreshold);
        await DrainStartupAsync();
        Pipeline.Mode = RunMode.Fail;
        var definition = NewOneShot("exhausted-oneshot");
        await ScheduleAsync(definition);

        var elapsedSeconds = -(int)FirstFireDelay.TotalSeconds;
        var attempt = 0;
        foreach (var offset in new[] { 0, 60, 180 })
        {
            Advance(TimeSpan.FromSeconds(offset - elapsedSeconds));
            elapsedSeconds = offset;
            await AwaitSettledRunAsync(definition.Id, ++attempt);
        }

        Assert.True(
            Definitions.Get(definition.Id) is null,
            "The definition still exists after Akka.Reminders gave up on the occurrence.");
        Assert.False(await IsScheduledAsync(definition.Id));
        var tombstone = RequireTombstone(definition.Id);
        Assert.Equal("Failed", OutcomeOf(tombstone));
        Assert.Contains(FailureText, Text(tombstone, "lastError"), StringComparison.Ordinal);
        Assert.Equal(DefaultPrompt, Text(tombstone, "instructions"));
        Assert.Single(Alerts.Alerts, a => a.Type == "reminder.auto_disabled");
    }
}
