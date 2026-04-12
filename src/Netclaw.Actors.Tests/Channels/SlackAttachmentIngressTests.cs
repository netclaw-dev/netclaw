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
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using SlackNet.Blocks;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Regression tests for the cross-channel attachment ingress pipeline
/// implemented in <see cref="SlackThreadBindingActor"/>. Each test
/// exercises one of the nine pipeline steps end-to-end (audience/size/count
/// gates, download, scan, modality read, inbox write, announcement line,
/// DataContent inlining) against a stubbed HTTP handler, content scanner,
/// and reply client. None of these tests touch a live Slack connection.
/// </summary>
public sealed class SlackAttachmentIngressVisionTests : TestKit
{
    private static readonly byte[] FakePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private static readonly byte[] FakePdfBytes =
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"u8.ToArray();

    private static readonly byte[] FakeDocxBytes =
        "PK\u0003\u0004fake docx content"u8.ToArray();

    private readonly RecordingChatClient _chatClient = new();
    private readonly RecordingReplyClient _replyClient = new();
    private readonly ConfigurableFakeSlackFileHandler _httpHandler = new();
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(),
        $"netclaw-slack-attachment-tests-{Guid.NewGuid():N}"));

    public SlackAttachmentIngressVisionTests(ITestOutputHelper output) : base(output: output)
    {
        _paths.EnsureDirectoriesExist();
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(_paths);
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-vision-model",
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
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
        services.AddSingleton<SessionPipeline>();

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

    private IActorRef BuildGateway(
        string gatewayName,
        SlackChannelOptions? options = null,
        IContentScanner? scanner = null,
        ChannelAttachmentPolicy? publicOverride = null)
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        if (publicOverride is not null)
            profiles.Public.ChannelAttachments = publicOverride;

        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: options ?? new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowDirectMessages = true,
                BotToken = new SensitiveString("xoxb-fake-token")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: scanner ?? new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: profiles,
            ModelCapabilities: Host.Services.GetRequiredService<ModelCapabilities>(),
            Paths: _paths,
            HttpClient: httpClient);

        return Sys.ActorOf(SlackGatewayActor.CreateProps(deps), gatewayName);
    }

    [Fact]
    public async Task Pdf_in_dm_on_vision_capable_model_is_saved_to_inbox_and_inlined()
    {
        _httpHandler.RespondWith("application/pdf", FakePdfBytes);
        var gateway = BuildGateway("slack-gw-pdf-inline");

        var files = new List<SlackFileReference>
        {
            new("F123", "report.pdf", "application/pdf", FakePdfBytes.Length,
                "https://files.slack.com/files-pri/T1234-F123/report.pdf")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D1:2000"),
            ChannelId: new SlackChannelId("D1"),
            ThreadTs: null,
            EventTs: new SlackEventTs("2000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "please summarize",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_chatClient.ReceivedMessages,
                contents => contents.Any(c => c is TextContent t && t.Text.Contains("[attachment]", StringComparison.Ordinal))
                            && contents.Any(c => c is DataContent d && d.MediaType == "application/pdf"));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var announcement = _chatClient.ReceivedMessages
            .SelectMany(m => m)
            .OfType<TextContent>()
            .First(t => t.Text.Contains("[attachment]", StringComparison.Ordinal));
        Assert.Contains("report.pdf", announcement.Text, StringComparison.Ordinal);
        Assert.Contains("inlined=\"true\"", announcement.Text, StringComparison.Ordinal);
        Assert.Contains("path=\"inbox/report.pdf\"", announcement.Text, StringComparison.Ordinal);

        var sessionId = new SessionId("D1/2000.1");
        var inboxPath = Path.Combine(
            SessionDirectoryHelper.GetOrCreateInboxDirectory(sessionId, _paths.SessionsDirectory),
            "report.pdf");
        Assert.True(File.Exists(inboxPath), $"Expected inbox file at {inboxPath}");
    }

    [Fact]
    public async Task Docx_in_dm_is_path_only_with_format_not_inlineable_note()
    {
        _httpHandler.RespondWith(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            FakeDocxBytes);
        var gateway = BuildGateway("slack-gw-docx-path-only");

        var files = new List<SlackFileReference>
        {
            new("F888", "notes.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                FakeDocxBytes.Length,
                "https://files.slack.com/files-pri/T1234-F888/notes.docx")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D2:2100"),
            ChannelId: new SlackChannelId("D2"),
            ThreadTs: null,
            EventTs: new SlackEventTs("2100.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "please read this",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_chatClient.ReceivedMessages,
                contents => contents.Any(c => c is TextContent t
                    && t.Text.Contains("[attachment]", StringComparison.Ordinal)
                    && t.Text.Contains("notes.docx", StringComparison.Ordinal)));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var announcement = _chatClient.ReceivedMessages
            .SelectMany(m => m)
            .OfType<TextContent>()
            .First(t => t.Text.Contains("[attachment]", StringComparison.Ordinal)
                     && t.Text.Contains("notes.docx", StringComparison.Ordinal));
        Assert.Contains("inlined=\"false\"", announcement.Text, StringComparison.Ordinal);
        Assert.Contains("format not inlineable", announcement.Text, StringComparison.Ordinal);

        // Docx is not inlineable — no DataContent for it should have been
        // forwarded to the LLM.
        var docxDataContents = _chatClient.ReceivedMessages
            .SelectMany(m => m)
            .OfType<DataContent>()
            .Where(d => d.MediaType?.Contains("wordprocessingml", StringComparison.Ordinal) == true);
        Assert.Empty(docxDataContents);
    }

    [Fact]
    public async Task Docx_in_public_channel_is_rejected_pre_download()
    {
        // Force the channel audience to Public via explicit ChannelAudiences
        // mapping — the default audience heuristic would promote an
        // allowlisted channel to Team.
        var gateway = BuildGateway(
            "slack-gw-public-docx-reject",
            options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = true,
                AllowedChannelIds = ["C_PUB"],
                ChannelAudiences = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["C_PUB"] = "public"
                },
                BotToken = new SensitiveString("xoxb-fake-token")
            });

        var files = new List<SlackFileReference>
        {
            new("F999", "secret.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                FakeDocxBytes.Length,
                "https://files.slack.com/files-pri/T1234-F999/secret.docx")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("C_PUB:3000"),
            ChannelId: new SlackChannelId("C_PUB"),
            ThreadTs: new SlackThreadTs("3000.0"),
            EventTs: new SlackEventTs("3000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "<@UBOT> check this",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_replyClient.PostedMessages,
                m => m.Text.Contains("secret.docx", StringComparison.Ordinal));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // No HTTP request was made — the file was rejected before download.
        Assert.Equal(0, _httpHandler.RequestCount);
    }

    [Fact]
    public async Task Oversize_file_is_rejected_pre_download()
    {
        var gateway = BuildGateway("slack-gw-oversize-reject");
        const long oversizeBytes = 30L * 1024 * 1024; // 30 MiB, above 25 MiB default

        var files = new List<SlackFileReference>
        {
            new("FBIG", "huge.pdf", "application/pdf", oversizeBytes,
                "https://files.slack.com/files-pri/T1234-FBIG/huge.pdf")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D3:3100"),
            ChannelId: new SlackChannelId("D3"),
            ThreadTs: null,
            EventTs: new SlackEventTs("3100.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "here",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_replyClient.PostedMessages,
                m => m.Text.Contains("huge.pdf", StringComparison.Ordinal)
                  && m.Text.Contains("25", StringComparison.Ordinal));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _httpHandler.RequestCount);
    }

    [Fact]
    public async Task Too_many_attachments_rejects_entire_batch_but_forwards_text()
    {
        var gateway = BuildGateway("slack-gw-too-many");

        // Default MaxFilesPerMessage is 10; send 15.
        var files = Enumerable.Range(1, 15)
            .Select(i => new SlackFileReference(
                $"F{i}", $"img{i}.png", "image/png", FakePngBytes.Length,
                $"https://files.slack.com/files-pri/T1234-F{i}/img{i}.png"))
            .ToList();

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D4:3200"),
            ChannelId: new SlackChannelId("D4"),
            ThreadTs: null,
            EventTs: new SlackEventTs("3200.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "batch upload",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_replyClient.PostedMessages,
                m => m.Text.Contains("10 attachments per message", StringComparison.Ordinal));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _httpHandler.RequestCount);

        // Text content "batch upload" should still have reached the LLM.
        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_chatClient.ReceivedMessages,
                contents => contents.Any(c => c is TextContent t
                    && t.Text.Contains("batch upload", StringComparison.Ordinal)));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filename_collision_across_turns_produces_suffixed_path()
    {
        // Two messages in the same Slack thread (same ThreadTs) route to
        // the same SessionId ("{channelId}/{threadTs}") and therefore the
        // same inbox directory. Uploading the same filename twice must
        // land the second copy at photo_1.png without overwriting the
        // first.
        _httpHandler.RespondWith("image/png", FakePngBytes);
        var gateway = BuildGateway(
            "slack-gw-collision",
            options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = true,
                AllowedChannelIds = ["D5"],
                BotToken = new SensitiveString("xoxb-fake-token")
            });

        var threadTs = new SlackThreadTs("3300.0");
        var sessionId = new SessionId("D5/3300.0");
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(sessionId, _paths.SessionsDirectory);

        var file = new SlackFileReference(
            "F_COLLIDE", "photo.png", "image/png", FakePngBytes.Length,
            "https://files.slack.com/files-pri/T1234-F_COLLIDE/photo.png");

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("D5:3300.1"),
            ChannelId: new SlackChannelId("D5"),
            ThreadTs: threadTs,
            EventTs: new SlackEventTs("3300.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "<@UBOT> first",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: [file]));

        await AwaitAssertAsync(() =>
        {
            Assert.True(File.Exists(Path.Combine(inboxDir, "photo.png")));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Same thread, same filename — should land at photo_1.png without
        // overwriting the first file.
        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("D5:3300.2"),
            ChannelId: new SlackChannelId("D5"),
            ThreadTs: threadTs,
            EventTs: new SlackEventTs("3300.2"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "<@UBOT> second",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false,
            Files: [file with { Id = "F_COLLIDE_2" }]));

        await AwaitAssertAsync(() =>
        {
            Assert.True(File.Exists(Path.Combine(inboxDir, "photo_1.png")));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Scanner_rejection_surfaces_user_visible_reply_with_no_inbox_write()
    {
        _httpHandler.RespondWith("image/png", FakePngBytes);
        var gateway = BuildGateway(
            "slack-gw-scan-reject",
            scanner: new AlwaysBlockContentScanner("malware detected"));

        var files = new List<SlackFileReference>
        {
            new("F_BAD", "bad.png", "image/png", FakePngBytes.Length,
                "https://files.slack.com/files-pri/T1234-F_BAD/bad.png")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D6:3400"),
            ChannelId: new SlackChannelId("D6"),
            ThreadTs: null,
            EventTs: new SlackEventTs("3400.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "nasty",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_replyClient.PostedMessages,
                m => m.Text.Contains("bad.png", StringComparison.Ordinal)
                  && m.Text.Contains("malware", StringComparison.Ordinal));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var sessionId = new SessionId("D6/3400.1");
        var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(sessionId, _paths.SessionsDirectory);
        Assert.Empty(Directory.GetFiles(inboxDir));
    }

    [Fact]
    public async Task Download_failure_posts_stable_message_without_raw_exception_detail()
    {
        // Internal network details (IPs, hostnames) in exception messages must
        // not reach Slack users — only a stable generic message is safe to show.
        const string internalDetail = "192.168.99.1:443";
        _httpHandler.RespondWithException(
            new HttpRequestException($"Network unreachable: {internalDetail}"));
        var gateway = BuildGateway("slack-gw-dl-error");

        var files = new List<SlackFileReference>
        {
            new("F_DL_ERR", "report.pdf", "application/pdf", 1024,
                "https://files.slack.com/files-pri/T1234-F_DL_ERR/report.pdf")
        };

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D_DL_ERR:9000"),
            ChannelId: new SlackChannelId("D_DL_ERR"),
            ThreadTs: null,
            EventTs: new SlackEventTs("9000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "here",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: files));

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_replyClient.PostedMessages,
                m => m.Text.Contains("report.pdf", StringComparison.Ordinal));
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_replyClient.PostedMessages,
            m => m.Text.Contains(internalDetail, StringComparison.Ordinal));
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class ConfigurableFakeSlackFileHandler : DelegatingHandler
    {
        private int _requestCount;
        private string _contentType = "image/png";
        private byte[] _bytes = FakePngBytes;
        private Exception? _exceptionToThrow;

        public int RequestCount => _requestCount;

        public void RespondWith(string contentType, byte[] bytes)
        {
            _contentType = contentType;
            _bytes = bytes;
            _exceptionToThrow = null;
        }

        public void RespondWithException(Exception exception)
        {
            _exceptionToThrow = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);

            if (_exceptionToThrow is not null)
                return Task.FromException<HttpResponseMessage>(_exceptionToThrow);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingReplyClient : ISlackReplyClient
    {
        public List<SlackPostMessage> PostedMessages { get; } = [];

        public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        {
            PostedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task<string> PostThreadReplyWithTsAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        {
            PostedMessages.Add(message);
            return Task.FromResult("fake.ts");
        }

        public Task UpdateThreadMessageAsync(
            SlackChannelId channelId,
            string messageTs,
            string text,
            IReadOnlyList<Block>? blocks = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UploadFileToThreadAsync(
            SlackChannelId channelId,
            SlackThreadTs threadTs,
            string filePath,
            string? fileName = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly object _gate = new();
        private readonly List<IList<AIContent>> _messages = new();

        public IReadOnlyList<IList<AIContent>> ReceivedMessages
        {
            get
            {
                lock (_gate)
                    return _messages.ToList();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                foreach (var msg in messages)
                {
                    if (msg.Role == Microsoft.Extensions.AI.ChatRole.User)
                        _messages.Add(msg.Contents);
                }
            }

            return Task.FromResult(new ChatResponse([
                new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "ok")
            ]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class AlwaysBlockContentScanner(string message) : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(
            ReadOnlyMemory<byte> bytes,
            string fileName,
            string mimeType,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ContentScanResult.Rejected(
                ContentScanError.AntivirusDetection,
                message));
        }
    }

}
