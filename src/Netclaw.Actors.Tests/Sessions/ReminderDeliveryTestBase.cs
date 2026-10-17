// -----------------------------------------------------------------------
// <copyright file="ReminderDeliveryTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Reminders;
using Netclaw.Configuration;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared host and helpers for the tests of "A failed post on a reminder turn is visible to the operator"
/// (spec <c>netclaw-session</c>, issue #2535). The host records every alert that the sessions emit.
/// </summary>
public abstract class ReminderDeliveryTestBase : LlmSessionTestBase
{
    protected const string AlertTypeText = "reminder.delivery.failed";
    protected const string ReminderSlug = "check-pr";
    protected const string FireKey = "check-pr:1712000000000";

    // DeliveryRetryHandler.MaxRetries is internal to the session. The spec fixes the budget at two retries.
    protected const int DeliveryRetryBudget = 2;

    internal readonly FakeChatClient ChatClient = new();
    internal readonly ReminderManagerActorTests.TestNotificationSink Alerts = new();

    protected ReminderDeliveryTestBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>The session snapshots after every N-th journal event. A derived class picks the interval.</summary>
    protected virtual int SnapshotInterval => 50;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(ChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = SnapshotInterval,
                TitleGenerationInterval = 0
            },
            // Passivation is driven explicitly with ReceiveTimeout.Instance.
            IdleTimeout = TimeSpan.Zero
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        // Last registration wins. The session and the reminder manager both resolve the sink from DI.
        services.AddSingleton<IOperationalNotificationSink>(Alerts);
    }

    /// <summary>True for the system nudge that <c>DeliveryRetryHandler.BuildNudge</c> writes into the history.</summary>
    protected static bool IsDeliveryNudge(Microsoft.Extensions.AI.ChatMessage message)
        => message.Text.Contains("could not be delivered", StringComparison.Ordinal);

    protected int AlertCount => Alerts.Alerts.Count(a => a.Type == AlertTypeText);

    protected async Task<(Akka.TestKit.TestProbe Subscriber, TurnCompleted Completed)> RunTurnAsync(
        SessionId sessionId, MessageSource? source, string content = "Check the open pull requests")
    {
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subscriber-" + Guid.NewGuid().ToString("N"));
        await JoinAsync(sessionManager, subscriber, sessionId);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = source
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        return (subscriber, completed);
    }

    /// <summary>Runs one more turn on a session that exists already and waits for its reply.</summary>
    protected async Task<TurnCompleted> RunFollowUpTurnAsync(
        SessionId sessionId, Akka.TestKit.TestProbe subscriber, MessageSource? source, string content)
    {
        await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = source
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        return await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    // Ask keeps the join reply away from the test actor, which must see only the Terminated message.
    protected async Task JoinAsync(IActorRef sessionManager, Akka.TestKit.TestProbe subscriber, SessionId sessionId)
    {
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
    }

    protected async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
        => await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    protected async Task StopSessionAsync(SessionId sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        Sys.Stop(session);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);
    }

    protected void SendDeliveryFailed(
        SessionId sessionId, TurnNumber turn, DeliveryFailureKind kind, ChannelType channel = ChannelType.Mattermost)
        => ActorRegistry.Get<SessionManagerActorKey>().Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = turn,
            ChannelType = channel,
            FailureKind = kind,
            ErrorMessage = "Unauthorized"
        });

    // A later message reaches the session after the earlier DeliveryFailed, because both use the same path and
    // the mailbox keeps their order. When the new turn completes, the session handled the earlier feedback.
    protected async Task AwaitSessionHandledEarlierMessagesAsync(SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Barrier message"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sends a person's message and holds the model reply, so the session stays in <c>Processing</c>.
    /// Returns after the model call started, so every later message reaches the <c>Processing</c> handler.
    /// The caller releases the reply through the returned source.
    /// </summary>
    protected async Task<TaskCompletionSource> HoldSessionInProcessingAsync(SessionId sessionId)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callsBefore = ChatClient.CallCount;
        ChatClient.NextResponseGate = gate;
        await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hold the session in Processing"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await AwaitAssertAsync(() =>
        {
            Assert.True(ChatClient.CallCount > callsBefore, "The model call did not start.");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        return gate;
    }

    /// <summary>
    /// Waits for the first alert, then lets the session handle every earlier message, then asserts that the
    /// session raised no second alert. A wait that stops at the first alert cannot see a duplicate.
    /// </summary>
    protected async Task<OperationalAlert> AwaitExactlyOneAlertAsync(SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        await AwaitAssertAsync(() =>
        {
            Assert.NotEqual(0, AlertCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        await AwaitSessionHandledEarlierMessagesAsync(sessionId, subscriber);
        return Assert.Single(Alerts.Alerts, a => a.Type == AlertTypeText);
    }

    protected async Task<IReadOnlyList<HistoryRecord>> ReadHistoryAsync()
        => await Host.Services.GetRequiredService<ReminderHistoryStore>()
            .ReadAsync(new ReminderId(ReminderSlug), 10);

    /// <summary>
    /// Stores a snapshot for a session that does not exist yet, so the session restores from exactly this value.
    /// The journal stays empty, so the session replays no <c>TurnRecorded</c> event.
    /// </summary>
    protected async Task SeedSnapshotAsync(SessionId sessionId, SessionSnapshot snapshot)
    {
        var probe = CreateTestProbe("snapshot-seed-" + Guid.NewGuid().ToString("N"));
        AkkaPersistence.Instance.Apply(Sys).SnapshotStoreFor(null).Tell(
            new SaveSnapshot(new SnapshotMetadata($"session-{sessionId.Value}", 1, DateTime.UnixEpoch), snapshot),
            probe.Ref);
        await probe.ExpectMsgAsync<SaveSnapshotSuccess>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    protected static SessionSnapshot SnapshotOfOneTurn() => new()
    {
        TurnCount = 1,
        History =
        [
            new SerializableChatMessage { Role = ChatRole.User, Content = "Check the open pull requests" },
            new SerializableChatMessage { Role = ChatRole.Assistant, Content = "All open pull requests are green." }
        ],
        EligibleDeliveryTurnNumber = new TurnNumber(1)
    };

    protected static MessageSource ReminderSource(string fireKey) => new()
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
