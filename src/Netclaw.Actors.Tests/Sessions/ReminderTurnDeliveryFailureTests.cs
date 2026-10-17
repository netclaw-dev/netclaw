// -----------------------------------------------------------------------
// <copyright file="ReminderTurnDeliveryFailureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Reminders;
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
public sealed class ReminderTurnDeliveryFailureTests : LlmSessionTestBase
{
    private const string AlertTypeText = "reminder.delivery.failed";
    private const string ReminderSlug = "check-pr";
    private const string FireKey = "check-pr:1712000000000";

    private readonly FakeChatClient _chatClient = new();
    private readonly ReminderManagerActorTests.TestNotificationSink _alerts = new();

    public ReminderTurnDeliveryFailureTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = 50,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        // Last registration wins. The session and the reminder manager both resolve the sink from DI.
        services.AddSingleton<IOperationalNotificationSink>(_alerts);
    }

    [Fact]
    public async Task A_transport_failure_on_a_reminder_turn_logs_an_error_and_raises_one_alert()
    {
        // Arrange: a reminder fire runs and its reply is recorded.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-1");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        Assert.Equal(new ReminderId(FireKey), completed.SourceReminderId);

        // Act: the bot token is bad, so the post fails. Mattermost reports every post failure as TransportFailure.
        await EventFilter.Error(contains: ReminderSlug).ExpectAsync(1, () =>
        {
            SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: one alert names the reminder and the session. The session wrote no history row.
        var alert = await AwaitSingleAlertAsync();
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
        Assert.DoesNotContain(_alerts.Alerts, a => a.Type == AlertTypeText);
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
        Assert.DoesNotContain(_alerts.Alerts, a => a.Type == AlertTypeText);
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
            Assert.DoesNotContain(_alerts.Alerts, a => a.Type == AlertTypeText);
            Assert.Equal(new ReminderId(FireKey), completed.SourceReminderId);
        }

        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.ContentRejected);

        // Assert: the session gave up, so the operator hears about it once.
        var alert = await AwaitSingleAlertAsync();
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_session_that_recovered_from_its_journal_still_finds_the_reminder()
    {
        // Arrange: the reminder turn is recorded. The session stops before the post failure arrives.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-recovered");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        var session = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(session);
        Sys.Stop(session);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // The new session actor rebuilds its state from the journal. Only TurnRecorded.SourceReminderId survives.
        var recovered = CreateTestProbe("recovered-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitSingleAlertAsync();
        Assert.Equal(ReminderSlug, alert.Source);
        Assert.Equal(sessionId.Value, alert.Context!["sessionId"]);
    }

    // DeliveryRetryHandler.MaxRetries is internal to the session. The spec fixes the budget at two retries.
    private const int DeliveryRetryBudget = 2;

    private async Task<(Akka.TestKit.TestProbe Subscriber, TurnCompleted Completed)> RunTurnAsync(
        SessionId sessionId, MessageSource? source)
    {
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subscriber-" + Guid.NewGuid().ToString("N"));
        await JoinAsync(sessionManager, subscriber, sessionId);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Check the open pull requests",
            Source = source
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        return (subscriber, completed);
    }

    // Ask keeps the join reply away from the test actor, which must see only the Terminated message.
    private async Task JoinAsync(IActorRef sessionManager, Akka.TestKit.TestProbe subscriber, SessionId sessionId)
    {
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private void SendDeliveryFailed(SessionId sessionId, TurnNumber turn, DeliveryFailureKind kind)
        => ActorRegistry.Get<SessionManagerActorKey>().Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = turn,
            ChannelType = ChannelType.Mattermost,
            FailureKind = kind,
            ErrorMessage = "Unauthorized"
        });

    // A later message reaches the session after the earlier DeliveryFailed, because both use the same path and
    // the mailbox keeps their order. When the new turn completes, the session handled the earlier feedback.
    private async Task AwaitSessionHandledEarlierMessagesAsync(SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Barrier message"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<OperationalAlert> AwaitSingleAlertAsync()
    {
        OperationalAlert? found = null;
        await AwaitAssertAsync(() =>
        {
            found = Assert.Single(_alerts.Alerts, a => a.Type == AlertTypeText);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        return found!;
    }

    private async Task<IReadOnlyList<HistoryRecord>> ReadHistoryAsync()
        => await Host.Services.GetRequiredService<ReminderHistoryStore>()
            .ReadAsync(new ReminderId(ReminderSlug), 10);

    private static MessageSource ReminderSource(string fireKey) => new()
    {
        ChannelType = ChannelType.Mattermost,
        SenderId = new SenderId("reminder-system"),
        MessageId = fireKey,
        TurnId = new TurnId(fireKey),
        ReminderId = new ReminderId(fireKey),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("reminder")
        },
        ReceivedAt = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
    };
}
