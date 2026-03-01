using System.Net;
using System.Net.Http.Headers;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Integration tests that exercise the full Slack file pipeline without a live
/// Slack connection. Fakes everything above the routing policy (Slack events)
/// and below the session pipeline (LLM responses). Validates:
///
/// 1. Inbound: SlackInboundMessage with file reference → download → persist to
///    session media directory → DataContent reaches LLM context
/// 2. Outbound: FileOutput from session pipeline → SlackReplyClient upload
/// </summary>
public sealed class SlackFileFlowIntegrationTests : TestKit
{
    private static readonly byte[] FakePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private readonly ImageCapturingChatClient _chatClient = new();
    private readonly RecordingReplyClient _replyClient = new();
    private readonly FakeSlackFileHandler _httpHandler = new();

    public SlackFileFlowIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new SessionConfig
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5,
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text,
            TitleGenerationInterval = 0
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new ImageCapabilityResolver());
        services.AddSingleton<SessionPipeline>();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Inbound_image_file_is_downloaded_and_persisted_to_session_media()
    {
        // Arrange: set up the full Slack actor hierarchy with a real SessionPipeline
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowDirectMessages = true,
                BotToken = new SensitiveString("xoxb-fake-token")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            HttpClient: httpClient);

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gw-file-test");

        // Act: send a DM with an image file attachment
        var files = new List<SlackFileReference>
        {
            new("F123", "test-image.png", "image/png", FakePngBytes.Length,
                "https://files.slack.com/files-pri/T1234-F123/test-image.png")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D1:1000"),
            ChannelId: new SlackChannelId("D1"),
            ThreadTs: null,
            EventTs: new SlackEventTs("1000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "check this image",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        // Assert: wait for the LLM to respond and Slack reply to be posted
        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.PostedMessages.Count > 0,
                "Expected at least one Slack reply to be posted");
        }, duration: TimeSpan.FromSeconds(10));

        // Verify: the fake HTTP handler was called to download the file
        Assert.True(_httpHandler.RequestCount > 0, "Expected file download request");
        Assert.Contains("files.slack.com", _httpHandler.LastRequestUri?.Host ?? "");

        // Verify: the chat client received image content
        Assert.True(_chatClient.ReceivedImageContent,
            "Expected LLM to receive DataContent (image) in chat messages");

        // Verify: file was persisted to session media directory
        var sessionId = new SessionId("D1/1000.1");
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId);
        var mediaDir = Path.Combine(sessionDir, "media");
        Assert.True(Directory.Exists(mediaDir),
            $"Expected session media directory to exist: {mediaDir}");

        var mediaFiles = Directory.GetFiles(mediaDir);
        Assert.True(mediaFiles.Length > 0,
            $"Expected at least one file in session media directory: {mediaDir}");

        // Verify: the persisted file contains the expected bytes
        var persistedBytes = await File.ReadAllBytesAsync(mediaFiles[0]);
        Assert.Equal(FakePngBytes, persistedBytes);
    }

    [Fact]
    public async Task App_mention_with_file_only_is_downloaded_and_persisted()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = true,
                AllowDirectMessages = false,
                AllowedChannelIds = ["C_TEST"],
                BotToken = new SensitiveString("xoxb-fake-token")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            HttpClient: httpClient);

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gw-mention-test");

        // AppMention with only a bot mention (normalizes to empty) + file attachment
        var files = new List<SlackFileReference>
        {
            new("F456", "screenshot.png", "image/png", FakePngBytes.Length,
                "https://files.slack.com/files-pri/T1234-F456/screenshot.png")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("C_TEST:2000"),
            ChannelId: new SlackChannelId("C_TEST"),
            ThreadTs: null,
            EventTs: new SlackEventTs("2000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "<@UBOT>",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.PostedMessages.Count > 0,
                "Expected at least one Slack reply to be posted");
        }, duration: TimeSpan.FromSeconds(10));

        Assert.True(_httpHandler.RequestCount > 0, "Expected file download request");
        Assert.True(_chatClient.ReceivedImageContent,
            "Expected LLM to receive image content");

        var sessionId = new SessionId("C_TEST/2000.1");
        var mediaDir = Path.Combine(
            SessionDirectoryHelper.GetSessionDirectory(sessionId), "media");
        Assert.True(Directory.Exists(mediaDir),
            $"Expected session media directory: {mediaDir}");
        Assert.True(Directory.GetFiles(mediaDir).Length > 0,
            "Expected persisted media files");
    }

    [Fact]
    public async Task File_share_subtype_with_text_flows_through_full_pipeline()
    {
        // Slack file uploads arrive as messages with subtype "file_share".
        // Both the text and the image must reach the LLM.
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowDirectMessages = true,
                BotToken = new SensitiveString("xoxb-fake-token")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            HttpClient: httpClient);

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gw-fileshare-test");

        var files = new List<SlackFileReference>
        {
            new("F789", "photo.jpg", "image/jpeg", FakePngBytes.Length,
                "https://files.slack.com/F789/photo.jpg")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D2:3000"),
            ChannelId: new SlackChannelId("D2"),
            ThreadTs: null,
            EventTs: new SlackEventTs("3000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "check this out",
            Subtype: "file_share",
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.PostedMessages.Count > 0,
                "Expected at least one Slack reply to be posted");
        }, duration: TimeSpan.FromSeconds(10));

        Assert.True(_httpHandler.RequestCount > 0, "Expected file download request");
        Assert.True(_chatClient.ReceivedImageContent,
            "Expected LLM to receive image content from file_share message");

        var sessionId = new SessionId("D2/3000.1");
        var mediaDir = Path.Combine(
            SessionDirectoryHelper.GetSessionDirectory(sessionId), "media");
        Assert.True(Directory.Exists(mediaDir),
            $"Expected session media directory: {mediaDir}");
        Assert.True(Directory.GetFiles(mediaDir).Length > 0,
            "Expected persisted media files");
    }

    /// <summary>
    /// Fake HTTP handler that serves canned image bytes for Slack file download requests.
    /// </summary>
    private sealed class FakeSlackFileHandler : DelegatingHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequestUri = request.RequestUri;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakePngBytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Reply client that records all posted messages and file uploads for assertion.
    /// </summary>
    private sealed class RecordingReplyClient : ISlackReplyClient
    {
        public List<SlackPostMessage> PostedMessages { get; } = [];
        public List<(SlackChannelId ChannelId, SlackThreadTs ThreadTs, string FilePath, string? FileName)> UploadedFiles { get; } = [];

        public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        {
            PostedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task UploadFileToThreadAsync(
            SlackChannelId channelId, SlackThreadTs threadTs, string filePath,
            string? filename = null, CancellationToken cancellationToken = default)
        {
            UploadedFiles.Add((channelId, threadTs, filePath, filename));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Chat client that tracks whether it received image content in messages.
    /// </summary>
    private sealed class ImageCapturingChatClient : IChatClient
    {
        private int _callCount;
        public int CallCount => _callCount;
        public volatile bool ReceivedImageContent;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);

            foreach (var msg in messages)
            {
                if (msg.Contents.OfType<DataContent>().Any())
                    ReceivedImageContent = true;
            }

            var contents = new List<AIContent>
            {
                new TextContent($"[fake] I see your image (call #{_callCount})")
            };
            var response = new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                contents));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => CreateStreamingAsync(messages, options, cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Capability resolver that reports image input support so the modality gate
    /// doesn't strip DataContent from inbound messages.
    /// </summary>
    private sealed class ImageCapabilityResolver : IModelCapabilityResolver
    {
        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
            => Task.FromResult<ResolvedModelCapabilities?>(
                new ResolvedModelCapabilities(
                    modelId,
                    ModelModality.Text | ModelModality.Image,
                    ModelModality.Text));
    }
}
