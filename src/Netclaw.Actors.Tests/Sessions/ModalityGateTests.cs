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
using Netclaw.Actors.Sessions;
using Xunit;
using Xunit.Abstractions;

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
        services.AddSingleton(new SessionConfig
        {
            ModelId = "text-only-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text, // no Image flag
            TitleGenerationInterval = 0,
            MemorySidecarsEnabled = false
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Image_with_text_strips_images_and_sends_text_to_llm()
    {
        var sessionId = new SessionId("test-channel/modality-text-only");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("modality-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

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
        }, TimeSpan.FromSeconds(5));

        // Should receive the "images removed" acknowledgement
        var ack = await subscriber.ExpectMsgAsync<TextOutput>();
        Assert.Contains("Images removed", ack.Text);

        // The LLM should still be called with the text content
        var textOutput = await subscriber.ExpectMsgAsync<TextOutput>();
        Assert.Contains("fake", textOutput.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // LLM was called (text was sent through despite images being stripped)
        Assert.Equal(1, _fakeChatClient.CallCount);
    }

    [Fact]
    public async Task Image_only_message_skips_llm_call_entirely()
    {
        var sessionId = new SessionId("test-channel/modality-image-only");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("modality-image-only-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "", // no text content
            MediaReferences =
            {
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = "image/png",
                    Modality = (int)MediaModality.Image
                }
            }
        }, TimeSpan.FromSeconds(5));

        // Should receive the "images removed" acknowledgement first
        var stripped = await subscriber.ExpectMsgAsync<TextOutput>();
        Assert.Contains("Images removed", stripped.Text);

        // Then the "only images" explanation
        var explanation = await subscriber.ExpectMsgAsync<TextOutput>();
        Assert.Contains("only images", explanation.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // LLM was NOT called
        Assert.Equal(0, _fakeChatClient.CallCount);
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
        services.AddSingleton(new SessionConfig
        {
            ModelId = "vision-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text | ModelModality.Image, // supports vision
            TitleGenerationInterval = 0,
            MemorySidecarsEnabled = false
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
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
        }, TimeSpan.FromSeconds(5));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

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
        }, TimeSpan.FromSeconds(5));

        // Should NOT receive an "images removed" acknowledgement — go straight to LLM response
        var textOutput = await subscriber.ExpectMsgAsync<TextOutput>();
        Assert.Contains("fake", textOutput.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Images removed", textOutput.Text);

        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // LLM was called
        Assert.Equal(1, _fakeChatClient.CallCount);
    }
}
