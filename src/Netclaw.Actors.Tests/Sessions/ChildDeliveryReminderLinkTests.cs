// -----------------------------------------------------------------------
// <copyright file="ChildDeliveryReminderLinkTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Reminders;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Spec <c>netclaw-session</c>, "A child-delivery turn keeps the reminder link of the turn that started the child".
/// A reminder turn that calls <c>spawn_agent</c> posts only the acceptance. The real reply comes in a later
/// child-delivery turn, which has no input of its own. That turn must keep the reminder link, or a failed post of
/// the real reply stays invisible. The link must survive a journal replay and a snapshot restore, both while the
/// child runs and after the child-delivery turn.
/// </summary>
public abstract class ChildDeliveryReminderLinkTestBase : ChildLifetimeTestBase
{
    protected const string AlertTypeText = "reminder.delivery.failed";
    protected const string ReminderSlug = "check-pr";
    protected const string FireKey = "check-pr:1712000000000";

    internal readonly ReminderManagerActorTests.TestNotificationSink Alerts = new();

    protected ChildDeliveryReminderLinkTestBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>The session snapshots after every N-th journal event. Zero turns snapshots off.</summary>
    protected abstract int SnapshotInterval { get; }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        // The last registration wins.
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { SnapshotInterval = SnapshotInterval, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<IOperationalNotificationSink>(Alerts);
    }

    protected int AlertCount => Alerts.Alerts.Count(a => a.Type == AlertTypeText);

    protected static async Task<TurnCompleted> NextTurnAsync(Akka.TestKit.TestProbe subscriber)
        => await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, Deadline, cancellationToken: Cancellation);

    protected void SendDeliveryFailed(SessionId sessionId, TurnNumber turn, DeliveryFailureKind kind)
        => SessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = turn,
            ChannelType = ChannelType.Slack,
            FailureKind = kind,
            ErrorMessage = "Unauthorized"
        }, Akka.Actor.ActorRefs.NoSender);

    /// <summary>
    /// A person's message reaches the session after every earlier <see cref="DeliveryFailed"/>, because both use
    /// the same path. When its turn completes, the session handled the earlier reports.
    /// </summary>
    protected async Task AwaitSessionHandledEarlierMessagesAsync(SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        await SendRequesterAsync(sessionId, "Barrier message");
        await NextTurnAsync(subscriber);
    }

    protected async Task<OperationalAlert> AwaitExactlyOneAlertAsync(SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        await AwaitAssertAsync(() => Assert.NotEqual(0, AlertCount), Deadline, cancellationToken: Cancellation);
        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);
        return Assert.Single(Alerts.Alerts, a => a.Type == AlertTypeText);
    }

    /// <summary>
    /// The reminder turn calls <c>spawn_agent</c>. The child waits on the returned gate, so the reminder turn
    /// completes first and records only the acceptance.
    /// </summary>
    protected async Task<(Akka.TestKit.TestProbe Subscriber, TurnCompleted Accepting, TaskCompletionSource ChildGate)>
        StartChildFromReminderAsync(SessionId sessionId)
    {
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);
        await SendReminderAsync(sessionId, FireKey);
        var accepting = await NextTurnAsync(subscriber);
        Assert.Equal(new ReminderId(FireKey), accepting.SourceReminderId);
        Assert.Single(accepting.OpenChildRunIds);
        return (subscriber, accepting, childGate);
    }
}

public sealed class ChildDeliveryReminderLinkTests : ChildDeliveryReminderLinkTestBase
{
    public ChildDeliveryReminderLinkTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override int SnapshotInterval => 0;

    [Fact]
    public async Task A_failed_post_of_the_child_result_of_a_reminder_turn_raises_one_alert()
    {
        // Arrange
        var sessionId = new SessionId("boundary/child-result-reminder-live");
        var (subscriber, accepting, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            Assert.Equal(accepting.TurnNumber.Value + 1, delivery.TurnNumber.Value);
            Assert.Empty(delivery.OpenChildRunIds);
            // The output of the child-delivery turn does not change. Only the journal record keeps the link.
            Assert.Null(delivery.SourceReminderId);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert
            var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
            Assert.Equal(ReminderSlug, alert.Source);
            Assert.Equal(sessionId.Value, alert.Context!["sessionId"]);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_failed_post_of_the_child_result_of_a_person_turn_raises_no_alert()
    {
        // Arrange: a person started the child, so the child result has no reminder link.
        var sessionId = new SessionId("boundary/child-result-person-live");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);
        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            await NextTurnAsync(subscriber);
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);
            await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

            // Assert
            Assert.Equal(0, AlertCount);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_spent_retry_budget_on_the_child_result_of_a_reminder_turn_raises_one_alert()
    {
        // Arrange: the child result of a spawn_agent child is an ordinary turn, so a retryable kind retries.
        // A retry turn has no input and no child, so it must keep the link of the turn that it repeats.
        var sessionId = new SessionId("boundary/child-result-reminder-retry-budget");
        var (subscriber, _, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            childGate.TrySetResult();
            var current = await NextTurnAsync(subscriber);

            // Act: the channel rejects the child result and each of the two retry replies.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var callsBefore = FakeChat.CallCount;
                SendDeliveryFailed(sessionId, current.TurnNumber, DeliveryFailureKind.ContentRejected);
                current = await NextTurnAsync(subscriber);
                Assert.True(FakeChat.CallCount > callsBefore, "The session did not retry on the main model.");
                Assert.Equal(0, AlertCount);
            }

            SendDeliveryFailed(sessionId, current.TurnNumber, DeliveryFailureKind.ContentRejected);

            // Assert: the session gave up, so the operator hears about it once.
            var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_child_that_the_session_recovered_from_its_journal_keeps_the_reminder_link()
    {
        // Arrange: the session stops while the child runs. Recovery replays the acceptance event, records the
        // child as lost, and starts the child-delivery turn at once.
        var sessionId = new SessionId("boundary/child-result-reminder-journal-replay");
        var (_, _, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            await StopSessionAsync(sessionId);
            var recovered = await JoinFullAsync(sessionId);
            var delivery = await NextTurnAsync(recovered);
            Assert.Empty(delivery.OpenChildRunIds);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert
            var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_child_delivery_turn_that_the_session_replayed_keeps_the_reminder_link()
    {
        // Arrange: the journal holds the child-delivery turn. Replay must read the link from that record.
        var sessionId = new SessionId("boundary/child-result-reminder-turn-replay");
        var (subscriber, _, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            await StopSessionAsync(sessionId);
            var recovered = await JoinFullAsync(sessionId);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert
            var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    // --- The link belongs to the turn that STARTED the child, not to the latest recorded turn ---

    [Fact]
    public async Task A_person_turn_between_the_reminder_start_and_the_child_result_does_not_move_the_link()
    {
        // Arrange: a reminder starts the child. A person speaks while the child runs, so the latest recorded turn
        // is a person turn when the child result arrives.
        var sessionId = new SessionId("boundary/child-result-person-between");
        var (subscriber, _, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            await SendRequesterAsync(sessionId, "A person speaks while the child runs.");
            var person = await NextTurnAsync(subscriber);
            Assert.Null(person.SourceReminderId);
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            Assert.Empty(delivery.OpenChildRunIds);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert: the child result still belongs to the reminder.
            var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_reminder_turn_between_the_person_start_and_the_child_result_does_not_move_the_link()
    {
        // Arrange: a person starts the child. A reminder fires while the child runs, so the latest recorded turn
        // is a reminder turn when the child result arrives.
        var sessionId = new SessionId("boundary/child-result-reminder-between");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-1", "child-a")];
        var subscriber = await JoinFullAsync(sessionId);
        try
        {
            await SendRequesterAsync(sessionId, "Start the child run.");
            var start = await NextTurnAsync(subscriber);
            Assert.Single(start.OpenChildRunIds);
            await SendReminderAsync(sessionId, FireKey);
            var reminder = await NextTurnAsync(subscriber);
            Assert.Equal(new ReminderId(FireKey), reminder.SourceReminderId);
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            Assert.Empty(delivery.OpenChildRunIds);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);
            await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

            // Assert: the child result belongs to the person, so the reminder gets no alert.
            Assert.Equal(0, AlertCount);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    // --- A child that a turn without an input starts keeps the link ---
    // A retry turn cannot start a child: a tool call in a retry turn fails with "Tool batch dispatch requires
    // admitted turn authority" (the guard is on dev, too). So the child-delivery turn is the only turn with no input
    // that starts a child, and this test is the proof for the inherited link.

    [Fact]
    public async Task A_child_that_a_child_delivery_turn_started_keeps_the_reminder_link()
    {
        // Arrange: the reminder turn starts child 1. The child-delivery turn of child 1 starts child 2.
        // Plan of the main model: call 1 starts child 1, call 2 is the acceptance, call 3 is the delivery turn
        // and starts child 2, call 4 is its acceptance.
        var sessionId = new SessionId("boundary/child-result-nested");
        foreach (var startsChild in new[] { true, false, true, false })
            FakeChat.PlannedToolCallDecisions.Enqueue(startsChild);
        FakeChat.AfterToolCallResponse = _ => FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-2", "child-a")];
        var (subscriber, _, firstGate) = await StartChildFromReminderAsync(sessionId);
        var secondGate = NewGate();
        try
        {
            await AwaitChildCallsAsync(1);
            Clients.Child.NextResponseGate = secondGate;
            firstGate.TrySetResult();
            var firstDelivery = await NextTurnAsync(subscriber);
            Assert.Single(firstDelivery.OpenChildRunIds);
            secondGate.TrySetResult();
            var secondDelivery = await NextTurnAsync(subscriber);
            Assert.Empty(secondDelivery.OpenChildRunIds);

            // Act
            SendDeliveryFailed(sessionId, secondDelivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert: child 2 has no input of its own to name the reminder, so it must inherit the link of child 1.
            var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            firstGate.TrySetResult();
            secondGate.TrySetResult();
        }
    }

    // --- One fire, two replies, two alerts (spec: at most one alert for each recorded turn) ---

    [Fact]
    public async Task One_fire_raises_one_alert_for_the_acceptance_post_and_one_for_the_result_post()
    {
        // Arrange: the reminder turn posts the acceptance, and the child-delivery turn posts the result.
        // The operator needs both facts, so the session does not merge the two reports.
        var sessionId = new SessionId("boundary/child-result-two-alerts");
        var (subscriber, accepting, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            // Act: the acceptance post fails.
            SendDeliveryFailed(sessionId, accepting.TurnNumber, DeliveryFailureKind.TransportFailure);
            await AwaitAssertAsync(() => Assert.Equal(1, AlertCount), Deadline, cancellationToken: Cancellation);

            // Act: the result post fails.
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);
            await AwaitAssertAsync(() => Assert.Equal(2, AlertCount), Deadline, cancellationToken: Cancellation);
            await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);

            // Assert: two alerts for one fire, one for each reply. A repeat report for the same turn adds none.
            Assert.Equal(2, AlertCount);
            Assert.All(Alerts.Alerts.Where(a => a.Type == AlertTypeText), a => Assert.Equal(ReminderSlug, a.Source));
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    // --- A new input during the retry of a child-delivery turn ends the link, and the session says so ---

    [Fact]
    public async Task A_new_input_during_the_retry_of_a_child_result_writes_a_warning_and_raises_no_alert()
    {
        // Arrange: the channel rejects the child result, and the retry turn waits on the main model.
        var sessionId = new SessionId("boundary/child-result-retry-cleared");
        var (subscriber, _, childGate) = await StartChildFromReminderAsync(sessionId);
        var retryGate = NewGate();
        try
        {
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            var callsBefore = FakeChat.CallCount;
            FakeChat.NextResponseGate = retryGate;
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.ContentRejected);
            await AwaitAssertAsync(
                () => Assert.True(FakeChat.CallCount > callsBefore, "The session did not start the retry turn."),
                Deadline, cancellationToken: Cancellation);

            // Act: a person speaks during the retry. The session buffers the input, and that input ends the retry link.
            await EventFilter.Warning(contains: $"ended the delivery retry of turn {delivery.TurnNumber} for reminder {FireKey}")
                .ExpectAsync(1, async () => await SendRequesterAsync(sessionId, "A person speaks during the retry."),
                    cancellationToken: Cancellation);
            retryGate.TrySetResult();
            await NextTurnAsync(subscriber);
            await NextTurnAsync(subscriber);

            // Assert: the Warning is the only signal. The person is in the thread, so the session raises no alert.
            Assert.Equal(0, AlertCount);
        }
        finally
        {
            childGate.TrySetResult();
            retryGate.TrySetResult();
        }
    }

    private async Task AwaitChildCallsAsync(int count)
        => await AwaitAssertAsync(
            () => Assert.True(Clients.Child.CallCount >= count, $"The child model made {Clients.Child.CallCount} of {count} calls."),
            Deadline, cancellationToken: Cancellation);
}

/// <summary>
/// The same link after a snapshot restore. With a snapshot after every journal event, the snapshot of the reminder
/// turn holds the accepted child, and the session does not replay the acceptance event.
/// </summary>
public sealed class ChildDeliveryReminderLinkSnapshotTests : ChildDeliveryReminderLinkTestBase
{
    public ChildDeliveryReminderLinkSnapshotTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override int SnapshotInterval => 1;

    [Fact]
    public async Task A_child_that_the_session_restored_from_a_snapshot_keeps_the_reminder_link()
    {
        // Arrange
        var sessionId = new SessionId("boundary/child-result-reminder-snapshot");
        var (_, _, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            await StopSessionAsync(sessionId);
            var recovered = await JoinFullAsync(sessionId);
            var delivery = await NextTurnAsync(recovered);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert
            var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_child_delivery_turn_that_the_session_restored_from_a_snapshot_keeps_the_reminder_link()
    {
        // Arrange: the child-delivery turn saves a snapshot. The restored session does not replay that turn.
        var sessionId = new SessionId("boundary/child-result-reminder-turn-snapshot");
        var (subscriber, _, childGate) = await StartChildFromReminderAsync(sessionId);
        try
        {
            childGate.TrySetResult();
            var delivery = await NextTurnAsync(subscriber);
            await StopSessionAsync(sessionId);
            var recovered = await JoinFullAsync(sessionId);

            // Act
            SendDeliveryFailed(sessionId, delivery.TurnNumber, DeliveryFailureKind.TransportFailure);

            // Assert
            var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
            Assert.Equal(ReminderSlug, alert.Source);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }
}
