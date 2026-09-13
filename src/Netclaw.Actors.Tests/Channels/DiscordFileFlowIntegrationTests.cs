// -----------------------------------------------------------------------
// <copyright file="DiscordFileFlowIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Sessions;
using FakeChatClient = Netclaw.Tests.Utilities.FakeChatClient;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Integration tests that exercise the Discord attachment ingress pipeline
/// end-to-end without a live Discord connection. Mirrors
/// <see cref="SlackFileFlowIntegrationTests"/> for the Discord adapter.
/// </summary>
public sealed class DiscordFileFlowIntegrationTests : TestKit
{
    private static readonly byte[] FakePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private readonly FakeChatClient _chatClient = new();
    private readonly RecordingDiscordReplyClient _replyClient = new();
    private readonly FakeDiscordFileHandler _httpHandler = new();
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(),
        $"netclaw-discord-file-tests-{Guid.NewGuid():N}"));

    public DiscordFileFlowIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
        _paths.EnsureDirectoriesExist();
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(_paths);
        services.AddSingleton<Netclaw.Actors.Jobs.BackgroundJobDefinitionStore>();
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new ImageCapabilityResolver());
        services.AddSingleton<SessionPipeline>();

        services.AddLlmSessionCompositeRecords();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Inbound_image_attachment_is_downloaded_and_persisted_to_session_media()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = CreateDependencies(pipeline, httpClient);

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gw-file-test");

        var attachments = new List<DiscordFileReference>
        {
            new("test-image.png", "image/png", FakePngBytes.Length,
                "https://cdn.discordapp.com/attachments/123/456/test-image.png")
        };

        gateway.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("msg-1000"),
            ChannelId: new DiscordChannelId("ch-1"),
            ReplyChannelId: new DiscordReplyChannelId("ch-1"),
            MessageId: new DiscordMessageId("msg-1000"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("msg-1000"),
            RootMessageId: new DiscordMessageId("msg-1000"),
            SenderId: new DiscordUserId("u-human"),
            IsBotMessage: false,
            IsDirectMessage: true,
            ContainsBotMention: false,
            Text: "check this image",
            ReceivedAt: TimeProvider.System.GetUtcNow(),
            Attachments: attachments));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.Posts.Count > 0,
                "Expected at least one Discord reply to be posted");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(_httpHandler.RequestCount > 0, "Expected file download request");
        Assert.Contains("cdn.discordapp.com", _httpHandler.LastRequestUri?.Host ?? "");

        Assert.True(
            _chatClient.LastReceivedMessages is not null
                && _chatClient.LastReceivedMessages.SelectMany(m => m.Contents).OfType<DataContent>().Any(),
            "Expected LLM to receive DataContent (image) in chat messages");

        var sessionId = new SessionId("ch-1/msg-1000");
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId, _paths.SessionsDirectory);
        var mediaDir = Path.Combine(sessionDir, "media");
        Assert.True(Directory.Exists(mediaDir),
            $"Expected session media directory to exist: {mediaDir}");

        var mediaFiles = Directory.GetFiles(mediaDir);
        Assert.True(mediaFiles.Length > 0,
            $"Expected at least one file in session media directory: {mediaDir}");

        var persistedBytes = await File.ReadAllBytesAsync(mediaFiles[0], TestContext.Current.CancellationToken);
        Assert.Equal(FakePngBytes, persistedBytes);
    }

    [Fact]
    public async Task Attachment_only_message_flows_through_pipeline()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = CreateDependencies(pipeline, httpClient);

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gw-attachment-only");

        var attachments = new List<DiscordFileReference>
        {
            new("screenshot.png", "image/png", FakePngBytes.Length,
                "https://cdn.discordapp.com/attachments/123/789/screenshot.png")
        };

        gateway.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("msg-2000"),
            ChannelId: new DiscordChannelId("ch-2"),
            ReplyChannelId: new DiscordReplyChannelId("ch-2"),
            MessageId: new DiscordMessageId("msg-2000"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("msg-2000"),
            RootMessageId: new DiscordMessageId("msg-2000"),
            SenderId: new DiscordUserId("u-human"),
            IsBotMessage: false,
            IsDirectMessage: true,
            ContainsBotMention: false,
            Text: "",
            ReceivedAt: TimeProvider.System.GetUtcNow(),
            Attachments: attachments));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.Posts.Count > 0,
                "Expected at least one Discord reply to be posted");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(_httpHandler.RequestCount > 0, "Expected file download request");
        Assert.True(
            _chatClient.LastReceivedMessages is not null
                && _chatClient.LastReceivedMessages.SelectMany(m => m.Contents).OfType<DataContent>().Any(),
            "Expected LLM to receive image content from attachment-only message");
    }

    [Fact]
    public async Task Scanner_failure_rejects_attachment_and_does_not_inline()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = CreateDependencies(pipeline, httpClient,
            contentScanner: new FailingContentScanner());

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gw-scanner-fail");

        var attachments = new List<DiscordFileReference>
        {
            new("drawing.png", "image/png", FakePngBytes.Length,
                "https://cdn.discordapp.com/attachments/123/999/drawing.png")
        };

        gateway.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("msg-3000"),
            ChannelId: new DiscordChannelId("ch-3"),
            ReplyChannelId: new DiscordReplyChannelId("ch-3"),
            MessageId: new DiscordMessageId("msg-3000"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("msg-3000"),
            RootMessageId: new DiscordMessageId("msg-3000"),
            SenderId: new DiscordUserId("u-human"),
            IsBotMessage: false,
            IsDirectMessage: true,
            ContainsBotMention: false,
            Text: "my drawing",
            ReceivedAt: TimeProvider.System.GetUtcNow(),
            Attachments: attachments));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.Posts.Count > 0,
                "Expected at least one Discord reply to be posted");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(
            _chatClient.LastReceivedMessages is not null
                && _chatClient.LastReceivedMessages.SelectMany(m => m.Contents).OfType<DataContent>().Any(),
            "Expected LLM not to receive image when scanner fails");

        Assert.Contains(_replyClient.Posts,
            m => m.Text.Contains("Couldn't scan `drawing.png`", StringComparison.Ordinal));

        var sessionId = new SessionId("ch-3/msg-3000");
        var storage = new Netclaw.Actors.Protocol.TestSessionStorageResolver(_paths).Resolve(sessionId);
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(storage);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(storage);
        Assert.Empty(Directory.GetFiles(inboxDir));
        Assert.Empty(Directory.GetFiles(stagingDir));
    }

    [Fact]
    public async Task Image_with_real_scanner_flows_to_llm()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var deps = CreateDependencies(pipeline, httpClient,
            contentScanner: new MagicByteContentScanner(new ContentPolicy()));

        var gateway = Sys.ActorOf(DiscordGatewayActor.CreateProps(deps), "discord-gw-real-scanner");

        var attachments = new List<DiscordFileReference>
        {
            new("photo.png", "image/png", FakePngBytes.Length,
                "https://cdn.discordapp.com/attachments/123/111/photo.png")
        };

        gateway.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("msg-4000"),
            ChannelId: new DiscordChannelId("ch-4"),
            ReplyChannelId: new DiscordReplyChannelId("ch-4"),
            MessageId: new DiscordMessageId("msg-4000"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("msg-4000"),
            RootMessageId: new DiscordMessageId("msg-4000"),
            SenderId: new DiscordUserId("u-human"),
            IsBotMessage: false,
            IsDirectMessage: true,
            ContainsBotMention: false,
            Text: "check this photo",
            ReceivedAt: TimeProvider.System.GetUtcNow(),
            Attachments: attachments));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_replyClient.Posts.Count > 0,
                "Expected at least one Discord reply to be posted");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(
            _chatClient.LastReceivedMessages is not null
                && _chatClient.LastReceivedMessages.SelectMany(m => m.Contents).OfType<DataContent>().Any(),
            "Expected LLM to receive DataContent (image) via real MagicByteContentScanner");
    }

    private DiscordGatewayDependencies CreateDependencies(
        ISessionPipeline pipeline,
        HttpClient? httpClient = null,
        IContentScanner? contentScanner = null)
    {
        return new DiscordGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new DiscordChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowDirectMessages = true,
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(_replyClient),
            ReplyClient: _replyClient,
            ContentScanner: contentScanner ?? new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: new Netclaw.Actors.Protocol.TestSessionStorageResolver(_paths),
            BotUserId: new DiscordUserId("UBOT"),
            HttpClient: httpClient,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
    }

    private sealed class FailingContentScanner : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(
            ReadOnlyMemory<byte> content, string filename, string declaredMimeType,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ContentScanResult.Rejected(
                ContentScanError.ScanFailure,
                "Content scan failed: simulated scanner failure"));
        }
    }

    private sealed class FakeDiscordFileHandler : DelegatingHandler
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

    private sealed class RecordingDiscordReplyClient : IDiscordReplyClient
    {
        public List<DiscordPostMessage> Posts { get; } = [];

        public Task<DiscordPostResult> PostReplyAsync(DiscordPostMessage message, CancellationToken cancellationToken = default)
        {
            Posts.Add(message);

            DiscordPostResult result;
            if (message.CreateThreadOnMessage is not null)
            {
                var threadId = new DiscordReplyChannelId($"thread-{message.CreateThreadOnMessage.Value.Value}");
                result = new DiscordPostResult(CreatedThreadId: threadId);
            }
            else
            {
                result = DiscordPostResult.Default;
            }

            return Task.FromResult(result);
        }

        public Task SetThreadNameAsync(DiscordReplyChannelId threadChannelId, string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMessageAsync(DiscordReplyChannelId channelId, DiscordMessageId messageId, string text,
            bool removeComponents = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TriggerTypingAsync(DiscordReplyChannelId channelId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DiscordMessageId?> UploadFileAsync(DiscordFileUpload upload, CancellationToken cancellationToken = default)
            => Task.FromResult<DiscordMessageId?>(new DiscordMessageId("file-1"));
    }
}
