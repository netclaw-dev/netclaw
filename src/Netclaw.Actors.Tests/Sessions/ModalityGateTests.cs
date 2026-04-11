using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests the modality gate in <see cref="LlmSessionActor"/>:
/// images sent to a text-only model are stripped; images sent to a vision model pass through.
/// </summary>
public class ModalityGateTextOnlyTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public ModalityGateTextOnlyTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "text-only-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text, // no Image flag
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        services.AddTestNetclawPaths();
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<NetclawPaths>()));
        services.AddSingleton(sp => new SessionMemoryServices(
            sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
            sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
            sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
            sp.GetService<SQLiteMemoryStore>()));
        services.AddSingleton(new SessionObservability(null, null));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Image_with_text_on_text_only_model_surfaces_ingress_bug_and_still_calls_llm()
    {
        // The strict-consumer contract treats an unsupported-modality media
        // ref reaching the session actor as an ingress bug. The session still
        // completes the turn (so the user gets a reply) but the offending refs
        // are dropped and a [system] notice about the ingress bug is appended
        // to the user message before it goes to the model. No legacy
        // "[Images removed]" placeholder is emitted.
        var sessionId = new SessionId("test-channel/modality-text-only");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("modality-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "What is in this picture?",
            MediaReferences =
            {
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = "image/png",
                    Modality = (int)MediaModality.Image
                }
            }
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The first output is the LLM response itself — there is no longer a
        // separate "[Images removed]" TextOutput before the reply.
        var textOutput = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", textOutput.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Images removed", textOutput.Text);

        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // LLM was called and saw the ingress-bug notice appended to the user text.
        Assert.Equal(1, _fakeChatClient.CallCount);
        Assert.NotEmpty(_fakeChatClient.ReceivedMessages);
        var lastRequest = _fakeChatClient.ReceivedMessages[^1];
        var concatenated = string.Join("\n", lastRequest.Select(m => m.Text ?? string.Empty));
        Assert.Contains("ingress bug", concatenated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Image_only_message_on_text_only_model_still_calls_llm_with_ingress_bug_notice()
    {
        // Empty text body + only unsupported media. The strict-consumer
        // contract appends the [system] ingress bug notice to the user
        // content so the LLM has something to respond to. We'd rather the
        // user get a reply explaining the situation than silence.
        var sessionId = new SessionId("test-channel/modality-image-only");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("modality-image-only-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "",
            MediaReferences =
            {
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = "image/png",
                    Modality = (int)MediaModality.Image
                }
            }
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var reply = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Images removed", reply.Text);

        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // LLM was called once, and the user-visible content we sent it
        // included the ingress-bug notice (not a legacy placeholder).
        Assert.Equal(1, _fakeChatClient.CallCount);
        Assert.NotEmpty(_fakeChatClient.ReceivedMessages);
        var lastRequest = _fakeChatClient.ReceivedMessages[^1];
        var concatenated = string.Join("\n", lastRequest.Select(m => m.Text ?? string.Empty));
        Assert.Contains("ingress bug", concatenated, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Tests that a vision-capable model accepts image references without stripping them.
/// </summary>
public class ModalityGateVisionTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public ModalityGateVisionTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "vision-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text | ModelModality.Image, // supports vision
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        services.AddTestNetclawPaths();
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<NetclawPaths>()));
        services.AddSingleton(sp => new SessionMemoryServices(
            sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
            sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
            sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
            sp.GetService<SQLiteMemoryStore>()));
        services.AddSingleton(new SessionObservability(null, null));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Image_passes_through_to_vision_model()
    {
        var sessionId = new SessionId("test-channel/modality-vision");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("vision-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Describe this image",
            MediaReferences =
            {
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = "image/png",
                    Modality = (int)MediaModality.Image
                }
            }
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Should NOT receive an "images removed" acknowledgement — go straight to LLM response
        var textOutput = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", textOutput.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Images removed", textOutput.Text);

        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // LLM was called
        Assert.Equal(1, _fakeChatClient.CallCount);
    }
}
