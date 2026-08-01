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
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
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
    public async Task Recovered_image_history_is_rejected_before_model_call()
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

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.InputCompatibility, error.Category);
        Assert.Contains("Image", error.Message, StringComparison.Ordinal);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Skipped, completed.Outcome);
        Assert.Equal(0, _chatClient.CallCount);
    }

    [Fact]
    public async Task Buffered_image_is_rejected_before_follow_up_model_call()
    {
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
        await AwaitAssertAsync(
            () => Assert.Equal(1, _chatClient.CallCount),
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Describe this buffered image.",
            MediaReferences = [ImageReference("buffered.png")]
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        responseGate.TrySetResult();

        var error = (ErrorOutput)await subscriber.FishForMessageAsync<object>(
            message => message is ErrorOutput,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.InputCompatibility, error.Category);

        var completed = await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);
        Assert.Equal(1, _chatClient.CallCount);
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
        }
    }

}
