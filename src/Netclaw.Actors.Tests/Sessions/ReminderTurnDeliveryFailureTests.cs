// -----------------------------------------------------------------------
// <copyright file="ReminderTurnDeliveryFailureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Spec: <c>netclaw-session</c>, "A failed post on a reminder turn is visible to the operator" (issue #2535, step B).
/// Since #2511 a current-session reminder run ends at admission, so the history row cannot show a failed post.
/// A Mattermost or Discord post failure is a <see cref="DeliveryFailureKind.TransportFailure"/>, which the session
/// does not retry. The session must report the failure itself, and it must find the reminder through the durable
/// <see cref="TurnRecorded.SourceReminderId"/> of the turn that failed to post.
/// </summary>
public sealed class ReminderTurnDeliveryFailureTests : ReminderDeliveryTestBase
{
    private const string UnknownTurnWarning = "cannot tell whether a reminder started turn";

    public ReminderTurnDeliveryFailureTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task A_transport_failure_on_a_reminder_turn_logs_an_error_and_raises_one_alert()
    {
        // Arrange: a reminder fire runs and its reply is recorded.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-1");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        Assert.Equal(new ReminderId(FireKey), completed.SourceReminderId);

        // Act: the bot token is bad, so the post fails. Mattermost reports every post failure as TransportFailure.
        await EventFilter.Error(contains: ReminderSlug).ExpectAsync(1, () =>
        {
            SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: one alert names the reminder and the session. The session wrote no history row.
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(AlertTypeText, alert.Type);
        Assert.Equal("ReminderDeliveryFailed", alert.Category.ToString());
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(ReminderSlug, alert.Source);
        var context = Assert.IsType<Dictionary<string, string>>(alert.Context);
        Assert.Equal(ReminderSlug, context["reminderId"]);
        Assert.Equal(sessionId.Value, context["sessionId"]);
        Assert.Equal(nameof(ChannelType.Mattermost), context["channel"]);
        Assert.Equal(nameof(DeliveryFailureKind.TransportFailure), context["failureKind"]);

        Assert.Empty(await ReadHistoryAsync());
    }

    [Fact]
    public async Task A_transport_failure_on_a_turn_that_no_reminder_started_raises_no_reminder_alert()
    {
        // Arrange: a person started the turn. A person in the thread can see that no reply arrived.
        var sessionId = new SessionId("mattermost/chan1/person-thread-1");
        var (subscriber, completed) = await RunTurnAsync(sessionId, source: null);
        Assert.Null(completed.SourceReminderId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

        // Assert
        Assert.Equal(0, AlertCount);
        Assert.Empty(await ReadHistoryAsync());
    }

    [Fact]
    public async Task A_retryable_failure_that_the_retry_recovers_raises_no_alert()
    {
        // Arrange: ContentRejected is retryable (DeliveryRetryHandler.IsRetryable). The session asks the model again.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-retry");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));

        // Act: the first post is rejected. The second reply posts, so no further failure comes.
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

        // Assert
        Assert.Equal(0, AlertCount);
    }

    [Fact]
    public async Task A_spent_retry_budget_on_a_reminder_turn_raises_one_alert()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-budget");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));

        // Act: the channel rejects the reply and each of the two retry replies.
        for (var attempt = 0; attempt < DeliveryRetryBudget; attempt++)
        {
            SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);
            await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, AlertCount);
            Assert.Equal(new ReminderId(FireKey), completed.SourceReminderId);
        }

        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert: the session gave up, so the operator hears about it once.
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_session_that_recovered_from_its_journal_still_finds_the_reminder()
    {
        // Arrange: the reminder turn is recorded. The session stops before the post failure arrives.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-recovered");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        await StopSessionAsync(sessionId);

        // The new session actor rebuilds its state from the journal. Only TurnRecorded.SourceReminderId survives.
        var recovered = CreateTestProbe("recovered-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
        Assert.Equal(sessionId.Value, alert.Context!["sessionId"]);
    }

    [Fact]
    public async Task A_recovered_ordinary_reminder_turn_still_retries_a_retryable_failure_on_the_main_model()
    {
        // Arrange: a turn that is not routed has RoutedSkill = false. An old journal entry has the same value,
        // because it has no routed mark. Replay must keep the retry for it.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-recovered-retry");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("recovered-retry-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert: the session asks the model again, and the retry reply posts, so there is no alert.
        await recovered.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var retried = await recovered.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(completed.TurnNumber.Value + 1, retried.TurnNumber.Value);
        Assert.Equal(0, AlertCount);
    }

    // --- B1: the turn number that a binding sends is the turn of the failed reply ---

    [Fact]
    public async Task A_failed_post_on_a_reminder_turn_after_an_earlier_turn_is_not_stale()
    {
        // Arrange: turn 1 belongs to a person. The reminder fire is turn 2. When N is 1, "N - 1" is 0, so a
        // single-turn session cannot tell a correct turn number from the previous one.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-second-turn");
        var (subscriber, first) = await RunTurnAsync(sessionId, source: null);
        var second = await RunFollowUpTurnAsync(sessionId, subscriber, ReminderSource(FireKey), "Check the open pull requests");
        Assert.Equal(first.TurnNumber.Value + 1, second.TurnNumber.Value);

        // Act: the binding names the turn of the failed reply.
        SendDeliveryFailed(sessionId, second.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_report_that_names_the_previous_turn_raises_no_alert_for_the_reminder_turn()
    {
        // Arrange: this is the number that the Mattermost and Discord bindings sent before the fix. The session
        // must treat it as feedback for the earlier turn. It must not blame the latest reminder turn.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-previous-turn");
        var (subscriber, first) = await RunTurnAsync(sessionId, source: null);
        await RunFollowUpTurnAsync(sessionId, subscriber, ReminderSource(FireKey), "Check the open pull requests");

        // Act
        SendDeliveryFailed(sessionId, first.TurnNumber, DeliveryFailureKind.TransportFailure);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

        // Assert
        Assert.Equal(0, AlertCount);
    }

    // --- S1: one reply raises at most one alert ---

    [Fact]
    public async Task Two_non_retryable_reports_for_one_reminder_turn_raise_one_alert()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-twice");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));

        // Act: one reply can fail more than once (a text chunk, then a file upload, then an error text).
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert: the wait helper settles the session before it counts, so it sees a second alert.
        await AwaitExactlyOneAlertAsync(sessionId, subscriber);
    }

    [Fact]
    public async Task A_report_while_processing_after_a_report_while_ready_raises_no_second_alert()
    {
        // Arrange: the first report finds the session Ready and raises the alert.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-ready-then-processing");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
        await AwaitAssertAsync(() =>
        {
            Assert.Equal(1, AlertCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Act: a person's message holds the session in Processing. The second report for the same turn arrives there.
        var gate = await HoldSessionInProcessingAsync(sessionId);
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
        gate.SetResult();
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, AlertCount);
    }

    // --- S3: the session never drops a report in silence ---

    [Fact]
    public async Task A_source_key_that_is_not_a_fire_key_raises_the_alert_with_the_whole_key_and_a_warning()
    {
        // Arrange: every production key comes from ReminderFireKey, so this breaks an invariant. The session
        // still raises the alert, and it makes the broken invariant visible.
        const string badKey = "not-a-fire-key";
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-bad-key");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(badKey));

        // Act
        await EventFilter.Warning(contains: "not a reminder fire key").ExpectAsync(1, () =>
        {
            SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(badKey, alert.Source);
    }

    [Fact]
    public async Task A_report_for_a_turn_the_session_does_not_know_logs_a_warning_and_raises_no_alert()
    {
        // Arrange: the session is Processing, so the report skips the Ready-state stale check.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-unknown-turn");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        var gate = await HoldSessionInProcessingAsync(sessionId);

        // Act: no turn with this number exists, so the session cannot tell whether a reminder started it.
        var unknownTurn = new TurnNumber(completed.TurnNumber.Value + 40);
        await EventFilter.Warning(contains: "cannot tell whether a reminder started turn").ExpectAsync(1, () =>
        {
            SendDeliveryFailed(sessionId, unknownTurn, DeliveryFailureKind.TransportFailure);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);

        gate.SetResult();
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, AlertCount);
    }

    [Fact]
    public async Task A_reported_turn_does_not_alert_again_after_the_session_passivated()
    {
        // Arrange: the session reported the failed reply, then it passivated with a final snapshot.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-reported-then-passivated");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
        await AwaitAssertAsync(() =>
        {
            Assert.Equal(1, AlertCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        session.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateTestProbe("reported-passivated-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act: a late second report for the same reply reaches the new incarnation. The snapshot is a new-format
        // snapshot, so the session knows the turn and that it has no unreported reminder. It returns quietly.
        await EventFilter.Warning(contains: UnknownTurnWarning).ExpectAsync(0, async () =>
        {
            SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
            await AwaitSessionHandledEarlierMessagesAsync(sessionId, recovered);
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: the snapshot did not hand the reminder back, so the new incarnation raises no second alert.
        await AwaitExactlyOneAlertAsync(sessionId, recovered);
    }

    // --- N1: a new-format snapshot lets the session recognize a turn that has no reminder ---

    [Fact]
    public async Task A_person_turn_that_the_session_restored_from_its_snapshot_returns_quietly()
    {
        // Arrange: a person started the turn. The session passivates and saves a final snapshot, which the
        // next incarnation restores. The snapshot names the eligible turn and holds no reminder.
        var sessionId = new SessionId("mattermost/chan1/person-thread-passivated");
        var (subscriber, completed) = await RunTurnAsync(sessionId, source: null);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        session.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateTestProbe("person-passivated-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        await EventFilter.Warning(contains: UnknownTurnWarning).ExpectAsync(0, async () =>
        {
            SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
            await AwaitSessionHandledEarlierMessagesAsync(sessionId, recovered);
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, AlertCount);
    }

    [Fact]
    public async Task A_new_format_snapshot_of_a_turn_with_no_reminder_returns_quietly()
    {
        // Arrange: the snapshot carries the routed mark (false), so it is a new-format snapshot. It holds no reminder.
        var sessionId = new SessionId("mattermost/chan1/seeded-new-format");
        await SeedSnapshotAsync(sessionId, SnapshotOfOneTurn() with { EligibleDeliveryTurnRoutedSkill = false });
        var subscriber = CreateTestProbe("seeded-new-format-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, sessionId);

        // Act
        await EventFilter.Warning(contains: UnknownTurnWarning).ExpectAsync(0, async () =>
        {
            SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.TransportFailure);
            await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, AlertCount);
    }

    [Fact]
    public async Task An_old_format_snapshot_still_gets_the_unknown_turn_warning()
    {
        // Arrange: the snapshot has no routed mark, so it was written before the delivery fields. The session cannot
        // tell what started the eligible turn, and it must say so. It must not invent a reminder.
        var sessionId = new SessionId("mattermost/chan1/seeded-old-format");
        await SeedSnapshotAsync(sessionId, SnapshotOfOneTurn());
        var subscriber = CreateTestProbe("seeded-old-format-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, sessionId);

        // Act
        await EventFilter.Warning(contains: UnknownTurnWarning).ExpectAsync(1, () =>
        {
            SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.TransportFailure);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);
        Assert.Equal(0, AlertCount);
    }

    [Fact]
    public async Task A_new_format_snapshot_of_a_reminder_turn_still_raises_the_alert()
    {
        // Arrange: the snapshot carries the reminder and the routed mark.
        var sessionId = new SessionId("mattermost/chan1/seeded-new-format-reminder");
        await SeedSnapshotAsync(sessionId, SnapshotOfOneTurn() with
        {
            EligibleDeliveryReminderId = new ReminderId(FireKey),
            EligibleDeliveryTurnRoutedSkill = false
        });
        var subscriber = CreateTestProbe("seeded-new-format-reminder-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), subscriber, sessionId);

        // Act
        SendDeliveryFailed(sessionId, new TurnNumber(1), DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_session_that_passivated_still_finds_the_reminder()
    {
        // Arrange: the reminder turn is recorded. The session passivates when idle and saves a final snapshot.
        // The next incarnation restores from that snapshot, so it does not replay the TurnRecorded event.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-passivated");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        session.Tell(new LeaveSession(subscriber) { SessionId = sessionId });
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateTestProbe("passivated-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
    }
}

/// <summary>
/// S3: a session that saves a snapshot on the reminder turn itself must keep what recovery needs. With a snapshot
/// after every journal event, the snapshot of the reminder turn is the newest one, and the journal events before
/// it are not replayed.
/// </summary>
public sealed class ReminderIntervalSnapshotRecoveryTests : ReminderDeliveryTestBase
{
    public ReminderIntervalSnapshotRecoveryTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override int SnapshotInterval => 1;

    [Fact]
    public async Task A_snapshot_taken_on_the_reminder_turn_keeps_the_turn_eligible_and_the_reminder()
    {
        // Arrange
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-interval-snapshot");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("interval-snapshot-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
    }
}
