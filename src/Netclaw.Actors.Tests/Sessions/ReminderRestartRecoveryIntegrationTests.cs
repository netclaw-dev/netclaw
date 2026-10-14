// -----------------------------------------------------------------------
// <copyright file="ReminderRestartRecoveryIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ReminderRestartRecoveryIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-03-21T12:00:00Z"));

    public ReminderRestartRecoveryIntegrationTests(ITestOutputHelper output) : base(output)
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
            IdleTimeout = TimeSpan.FromMinutes(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<TimeProvider>(_timeProvider);
    }

    [Fact]
    public async Task Reminder_origin_restart_restores_an_interrupted_input_without_a_second_user_prompt()
    {
        // Arrange
        var sessionId = new SessionId("reminder/restart-resume");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _chatClient.NextResponseGate = responseGate;
        var source = new MessageSource
        {
            ChannelType = ChannelType.Reminder,
            SenderId = new SenderId("reminder-system"),
            MessageId = "restart-input-1",
            TurnId = new TurnId("restart-turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            {
                SourceKind = new SourceKind("reminder")
            },
            ReceivedAt = _timeProvider.GetUtcNow(),
            ReminderId = new ReminderId("restart-input-1")
        };

        // Act
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Finish the interrupted task",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await _chatClient.FirstCallEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var child = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        var drainAck = await sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(8),
            TestContext.Current.CancellationToken);
        var reminder = Assert.IsType<ReminderDefinition>(drainAck.RestartReminder);
        await ExpectTerminatedAsync(
            child,
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        var subscriber = CreateTestProbe("restart-resume-subscriber");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);
        var dueTime = Assert.IsType<DateTimeOffset>(reminder.Schedule.FireAt);
        var occurrenceKey = new ReminderId($"{reminder.Id}:{dueTime.ToUnixTimeMilliseconds()}");
        var envelope = new Akka.Reminders.ReminderEnvelope<ReminderPayload>(
            new Akka.Reminders.ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
            new Akka.Reminders.ReminderKey(reminder.Id.Value),
            dueTime,
            Akka.Reminders.ReminderDeadline.Infinite,
            new ReminderPayload { Id = reminder.Id });
        var execution = ReminderExecutionActor.CreateProps(
            Guid.NewGuid(),
            reminder,
            Host.Services.GetRequiredService<SessionPipeline>(),
            _timeProvider,
            envelope);
        var outcome = CreateTestProbe("restart-reminder-outcome");
        Sys.ActorOf(Props.Create(() => new AcceptingReminderParent(outcome, execution)), "restart-reminder-execution");

        var completed = await outcome.ExpectMsgAsync<ReminderExecutionCompleted>(
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completed.Success, completed.ErrorMessage);
        var resumedTurn = await subscriber.FishForMessageAsync<TurnCompleted>(
            turn => turn.SourceReminderId == occurrenceKey,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DeliveryKind.CurrentSession, reminder.Delivery.Kind);
        Assert.Equal(sessionId.Value, reminder.Delivery.SessionId);
        Assert.Equal(ChannelType.Reminder, reminder.Delivery.OriginChannelType);
        Assert.True(reminder.DeliveryRequired);
        Assert.Equal(TrustAudience.Personal, reminder.Audience);
        Assert.Equal(TrustBoundary.Personal, reminder.Boundary);
        Assert.Equal(TimeSpan.FromMinutes(10), reminder.ExpiresAt - reminder.CreatedAt);
        Assert.Equal(sessionId.Value, completed.History.SessionId);
        Assert.Equal(occurrenceKey, resumedTurn.SourceReminderId);
        var resumedCall = _chatClient.ReceivedMessages.Last(conversation =>
            conversation.Any(message => string.Equals(message.Text, "Finish the interrupted task", StringComparison.Ordinal)));
        Assert.DoesNotContain(resumedCall, message => string.Equals(message.Text, reminder.Instructions, StringComparison.Ordinal));
        Assert.Equal(2, _chatClient.CallCount);
    }

    private sealed class AcceptingReminderParent : ReceiveActor
    {
        public AcceptingReminderParent(IActorRef observer, Props execution)
        {
            Context.ActorOf(execution, "execution");
            Receive<ReminderExecutionCompleted>(completed =>
            {
                observer.Tell(completed);
                Sender.Tell(new ReminderExecutionAccepted(completed.ExecutionId));
            });
        }
    }
}
