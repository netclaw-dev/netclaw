// -----------------------------------------------------------------------
// <copyright file="LlmSessionImageDeliveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// End-to-end guard for the tool → model image hand-off on the streaming
/// session path. A tool (e.g. <c>file_read</c>) that loads an image for
/// model-visible inspection registers it via
/// <see cref="Netclaw.Tools.ToolExecutionContext.AddModelInputFile"/>; the
/// session actor must carry those bytes into the NEXT LLM call as
/// <see cref="DataContent"/>.
///
/// Regression: the streaming completion path
/// (<c>LlmSessionActor.ApplyToolCallRecorded</c>) handed its mutable
/// <c>_pendingModelInputMediaReferences</c> accumulator to the media nudge and
/// then <c>Clear()</c>ed that same instance. Because the nudge aliased the list
/// instead of copying it, the image reference was wiped before the follow-up
/// LLM call hydrated it — the model was told "Image loaded" but never received
/// the bytes and hallucinated. This test drives a real streaming tool call
/// through the actor and asserts the image bytes reach the chat client on the
/// second call; it fails (no DataContent on call 2) without the defensive
/// snapshot in <c>SessionState</c>. See GitHub #1264.
/// </summary>
public class LlmSessionImageDeliveryTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly string _imagePath = Path.Combine(
        Path.GetTempPath(), $"netclaw-img-delivery-{Guid.NewGuid():N}.png");

    // Minimal but valid PNG header (signature + IHDR start) — enough to pass the
    // materialization magic-byte/MIME compatibility checks. Same bytes the
    // pipeline-level media tests use.
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    public LlmSessionImageDeliveryTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));

        // Vision-capable model — without Image input modality the materialization
        // step would (correctly) skip the file, so the gate must be open for this
        // test to exercise the delivery path.
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "vision-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Image | ModelModality.Text,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(new ImageRegisteringToolExecutor(_imagePath));

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string path) => $"contents of {path}", "file_read"),
            "file_read");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Tool_loaded_image_reaches_the_model_on_the_next_call()
    {
        await File.WriteAllBytesAsync(_imagePath, PngBytes, TestContext.Current.CancellationToken);
        try
        {
            // First LLM response is a file_read tool call; the second is text.
            _fakeChatClient.ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-1", "file_read",
                    new Dictionary<string, object?> { ["Path"] = _imagePath })
            ];

            var sessionId = new SessionId("test-channel/image-delivery");
            var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe("img-sub");

            await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
            {
                SessionId = sessionId,
                Filter = OutputFilter.Full
            }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Describe the image."
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Wait until the post-tool-result (second) LLM call has been issued.
            await AwaitAssertAsync(
                () => Assert.True(_fakeChatClient.CallCount >= 2,
                    $"expected >= 2 LLM calls, saw {_fakeChatClient.CallCount}"),
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);

            // The second call carries the conversation AFTER the tool loaded the
            // image. The image bytes must be present as DataContent — this is the
            // assertion the aliasing bug breaks.
            var secondCall = _fakeChatClient.ReceivedMessages[1];
            var images = secondCall
                .SelectMany(m => m.Contents)
                .OfType<DataContent>()
                .ToList();

            Assert.True(
                images.Any(dc => dc.MediaType == "image/png"),
                "Tool-loaded image never reached the model: no image/png DataContent on the " +
                "second LLM call. The media nudge lost its attachment between tool completion " +
                "and context assembly (see GitHub #1264).");
        }
        finally
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
        }
    }

    /// <summary>
    /// Stand-in for an image-loading tool (e.g. <c>file_read</c> on a PNG): it
    /// registers the file as model-visible input, exactly as the real tool does.
    /// </summary>
    private sealed class ImageRegisteringToolExecutor(string imagePath) : IToolExecutor
    {
        public Task AuthorizeAsync(
            FunctionCallContent toolCall,
            Netclaw.Tools.ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            Netclaw.Tools.ToolExecutionContext? context = null,
            CancellationToken ct = default)
        {
            context?.AddModelInputFile(imagePath, "diagram.png", "image/png");
            return Task.FromResult("Image loaded for model-visible inspection on the next LLM call.");
        }
    }
}
