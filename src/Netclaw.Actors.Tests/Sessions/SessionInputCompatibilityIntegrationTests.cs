// -----------------------------------------------------------------------
// <copyright file="SessionInputCompatibilityIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionInputCompatibilityIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();

    public SessionInputCompatibilityIntegrationTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "text-only-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
    }

    [Fact]
    public async Task Recovered_image_history_is_degraded_not_rejected()
    {
        var sessionId = new SessionId("test-channel/recovered-image-compatibility");
        var seeder = Sys.ActorOf(Props.Create(() => new SessionEventSeeder($"session-{sessionId.Value}")));
        await seeder.Ask<Done>(new TurnRecorded
        {
            SessionId = sessionId,
            UserMessage = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.User,
                Content = "Describe this image.",
                MediaReferences = [ImageReference("historical.png")]
            },
            AssistantReply = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.Assistant,
                Content = "A prior response."
            },
            RecordedAtMs = 1
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);

        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("recovered-image-compatibility-sub");
        var joined = await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, joined.TurnCount);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The session should proceed normally — the historical image is stripped
        // at assembly time, not rejected. The model is called with text-only content.
        var completed = await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(TurnOutcome.Skipped, completed.Outcome);
        Assert.NotEqual(TurnOutcome.Failed, completed.Outcome);
        Assert.True(_chatClient.CallCount >= 1);
    }

    [Fact]
    public async Task Recovery_restores_each_consumed_input_once_in_event_order()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/multi-input-replay");
        var firstId = new InputId("replay-first-id");
        var secondId = new InputId("replay-second-id");
        var firstUser = new SerializableChatMessage
        {
            Role = Netclaw.Actors.Protocol.ChatRole.User,
            Content = "replay-first-731"
        };
        var secondUser = new SerializableChatMessage
        {
            Role = Netclaw.Actors.Protocol.ChatRole.User,
            Content = "replay-second-842"
        };
        var context = new TurnContextRecord
        {
            SessionId = sessionId,
            ChannelType = "slack",
            RequesterSenderId = new SenderId("user-1")
        };
        var seeder = Sys.ActorOf(Props.Create(() => new SessionEventSeeder($"session-{sessionId.Value}")));
        await seeder.Ask<Done>(new InputAdmitted
        {
            SessionId = sessionId,
            InputId = firstId,
            SourceMessageId = firstId.Value,
            UserMessage = firstUser,
            TurnContext = context,
            AdmittedAtMs = 1
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await seeder.Ask<Done>(new InputAdmitted
        {
            SessionId = sessionId,
            InputId = secondId,
            SourceMessageId = secondId.Value,
            UserMessage = secondUser,
            TurnContext = context,
            AdmittedAtMs = 2
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await seeder.Ask<Done>(new TurnRecorded
        {
            SessionId = sessionId,
            UserMessage = secondUser,
            AssistantReply = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.Assistant,
                Content = "replay-answer-953"
            },
            ConsumedInputIds = [firstId, secondId],
            RecordedAtMs = 3
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("multi-input-replay-sub");
        var joined = await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "replay-followup-064"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var completed = await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var relevant = _chatClient.ReceivedMessages.Single()
            .Where(message => message.Text is "replay-first-731" or "replay-second-842"
                or "replay-answer-953" or "replay-followup-064")
            .Select(message => (message.Role, message.Text));

        // Assert
        Assert.Equal(1, joined.TurnCount);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(2, completed.TurnNumber.Value);
        Assert.Equal(new[]
        {
            (Microsoft.Extensions.AI.ChatRole.User, "replay-first-731"),
            (Microsoft.Extensions.AI.ChatRole.User, "replay-second-842"),
            (Microsoft.Extensions.AI.ChatRole.Assistant, "replay-answer-953"),
            (Microsoft.Extensions.AI.ChatRole.User, "replay-followup-064")
        }, relevant);
    }

    [Fact]
    public async Task Buffered_image_is_degraded_not_rejected()
    {
        // Gate the first model response so the buffered second message
        // drains after we release the gate — both TurnCompleted events
        // arrive deterministically rather than racing.
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _chatClient.NextResponseGate = responseGate;

        var sessionId = new SessionId("test-channel/buffered-image-compatibility");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("buffered-image-compatibility-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Describe this buffered image.",
            MediaReferences = [ImageReference("buffered.png")]
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Release the gate — first turn completes, drain fires, second call
        // proceeds with the image stripped at assembly time.
        responseGate.TrySetResult();

        for (var i = 0; i < 2; i++)
        {
            var completed = await subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(TurnOutcome.Failed, completed.Outcome);
        }

        Assert.Equal(2, _chatClient.CallCount);
    }

    private static SerializableMediaReference ImageReference(string path) => new()
    {
        RelativePath = path,
        MimeType = new Netclaw.Media.MimeType("image/png"),
        Modality = (int)MediaModality.Image
    };

    private sealed class SessionEventSeeder : ReceivePersistentActor
    {
        public override string PersistenceId { get; }

        public SessionEventSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });

            Command<TurnRecorded>(turn =>
            {
                var replyTo = Sender;
                Persist(turn, _ => replyTo.Tell(Done.Instance));
            });
            Command<InputAdmitted>(input =>
            {
                var replyTo = Sender;
                Persist(input, _ => replyTo.Tell(Done.Instance));
            });
        }
    }

}
