// -----------------------------------------------------------------------
// <copyright file="ModalityGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tests the modality gate in <see cref="LlmSessionActor"/>.
/// </summary>
public class ModalityGateTextOnlyTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();

    public ModalityGateTextOnlyTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "text-only-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text,
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
    }

    [Fact]
    public async Task Image_with_text_on_text_only_model_is_rejected_before_model_call()
    {
        var sessionId = new SessionId("test-channel/modality-text-only");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("modality-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "What is in this picture?",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = new Netclaw.Media.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                }
            ]
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.InputCompatibility, error.Category);
        Assert.Contains("Image", error.Message, StringComparison.Ordinal);
        Assert.Contains("text-only-model", error.Message, StringComparison.Ordinal);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Skipped, completed.Outcome);
        Assert.Equal(0, _fakeChatClient.CallCount);

        var joined = await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, joined.TurnCount);
        Assert.Empty(joined.RecentMessages ?? []);
    }

    [Fact]
    public async Task Image_only_message_on_text_only_model_is_rejected_before_model_call()
    {
        var sessionId = new SessionId("test-channel/modality-image-only");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("modality-image-only-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = new Netclaw.Media.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                }
            ]
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.InputCompatibility, error.Category);
        Assert.Contains("start a new conversation", error.Message, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Skipped, completed.Outcome);
        Assert.Equal(0, _fakeChatClient.CallCount);
    }
}

/// <summary>
/// Tests that a vision-capable model accepts image references without stripping them.
/// </summary>
public class ModalityGateVisionTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();

    public ModalityGateVisionTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "vision-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text | ModelModality.Image,
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
    }

    [Fact]
    public async Task Image_passes_through_to_vision_model()
    {
        var sessionId = new SessionId("test-channel/modality-vision");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("vision-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Describe this image",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = new Netclaw.Media.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                }
            ]
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
