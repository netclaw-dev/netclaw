// -----------------------------------------------------------------------
// <copyright file="SessionImageProxyRecoveryIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionImageProxyRecoveryIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();
    private readonly RecordingImageProxyAnalyzer _analyzer = new();

    public SessionImageProxyRecoveryIntegrationTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton<IImageProxyAnalyzer>(_analyzer);
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
            "You are a test assistant."));
    }

    [Fact]
    public async Task Recovered_image_gets_durable_analysis_before_main_call()
    {
        _analyzer.MainCallCount = () => _chatClient.CallCount;
        var sessionId = new SessionId("test-channel/recovered-image-proxy");
        var seeder = Sys.ActorOf(Props.Create(() => new SessionEventSeeder($"session-{sessionId.Value}")));
        await seeder.Ask<Done>(new TurnRecorded
        {
            SessionId = sessionId,
            UserMessage = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.User,
                Content = "Describe this image.",
                MediaReferences =
                [
                    new SerializableMediaReference
                    {
                        RelativePath = "historical.png",
                        MimeType = new Netclaw.Media.MimeType("image/png"),
                        Modality = (int)MediaModality.Image
                    }
                ]
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
        var subscriber = CreateTestProbe("recovered-image-proxy-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("historical.png", Assert.Single(_analyzer.Paths));
        Assert.Equal(1, _chatClient.CallCount);
        var mainRequest = Assert.Single(_chatClient.ReceivedMessages);
        Assert.Contains(mainRequest,
            message => message.Text.Contains("Recovered image description.", StringComparison.Ordinal));
    }

    private sealed class RecordingImageProxyAnalyzer : IImageProxyAnalyzer
    {
        public List<string> Paths { get; } = [];

        public Func<int> MainCallCount { get; set; } = () => 0;

        public bool IsEnabled => true;

        public Task<ImageProxyAnalysis> AnalyzeAsync(
            SessionId sessionId,
            SerializableMediaReference media,
            string sessionsBasePath,
            CancellationToken cancellationToken)
        {
            Assert.Equal(0, MainCallCount());
            Paths.Add(media.RelativePath);
            return Task.FromResult(new ImageProxyAnalysis
            {
                RelativePath = media.RelativePath,
                DefinitionName = "vision",
                ModelId = "qwen-vl",
                PromptVersion = "image-description-v1",
                Description = "Recovered image description.",
                AnalyzedAtMs = 1234
            });
        }
    }

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
