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
