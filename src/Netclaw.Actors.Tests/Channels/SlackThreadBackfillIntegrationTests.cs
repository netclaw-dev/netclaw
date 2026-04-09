using System.Net;
using System.Net.Http.Headers;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Integration tests for thread history backfill. Validates that when the bot
/// is @-mentioned in an existing Slack thread, prior messages (text + images)
/// are fetched and injected as context before the first LLM turn.
/// </summary>
public sealed class SlackThreadBackfillIntegrationTests : TestKit
{
    private static readonly byte[] FakePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private readonly ContextCapturingChatClient _chatClient = new();
    private readonly RecordingReplyClient _replyClient = new();
    private readonly FakeSlackFileHandler _httpHandler = new();
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(),
        $"netclaw-backfill-tests-{Guid.NewGuid():N}"));

    public SlackThreadBackfillIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
        _paths.EnsureDirectoriesExist();
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(_paths);
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
                MemorySidecarsEnabled = false,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IModelCapabilityResolver>(new ImageCapabilityResolver());
        services.AddSingleton<SessionPipeline>();
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<NetclawPaths>()));
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
    public async Task Backfill_text_and_images_appear_in_LLM_context_before_mention()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        // Fake thread history fetcher returns 2 prior messages (one with image)
        var fetcher = new SlackThreadHistoryFetcher(
            FakeRepliesFetcher,
            new SlackChannelOptions { BotToken = new SensitiveString("xoxb-fake") },
            httpClient,
            new NullContentScanner(),
            NullLogger<SlackThreadHistoryFetcher>.Instance);

        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = true,
                AllowedChannelIds = ["C_BACKFILL"],
                BotToken = new SensitiveString("xoxb-fake-token")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            HttpClient: httpClient,
            ThreadHistoryFetcher: fetcher);

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gw-backfill");

        // Mention the bot in an existing thread (ThreadTs != EventTs)
        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("C_BACKFILL:3000.3"),
            ChannelId: new SlackChannelId("C_BACKFILL"),
            ThreadTs: new SlackThreadTs("3000.0"),
            EventTs: new SlackEventTs("3000.3"),
            UserId: new SlackUserId("U_MENTIONER"),
            BotId: null,
            Text: "<@UBOT> can you help with this?",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false));

        // Wait for the LLM to be called
        await AwaitAssertAsync(() =>
        {
            Assert.True(_chatClient.CallCount > 0, "Expected at least one LLM call");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Verify: LLM received thread history context block before the mention
        var messages = _chatClient.LastMessages!;
        var userMessages = messages.Where(m => m.Role == AiChatRole.User).ToList();
        Assert.True(userMessages.Count >= 2, $"Expected at least 2 user messages (backfill + mention), got {userMessages.Count}");

        // First user message should be the thread history block
        var historyMsg = userMessages[0];
        var historyText = string.Join("", historyMsg.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("[thread history", historyText);
        Assert.Contains("[end thread history]", historyText);
        Assert.Contains("Has anyone looked at the dashboard?", historyText);
        Assert.Contains("I think it's the new query", historyText);

        // Thread history should include images
        Assert.True(historyMsg.Contents.OfType<DataContent>().Any(),
            "Expected backfill context to include image DataContent");

        // Last user message should be the actual mention
        var mentionMsg = userMessages[^1];
        var mentionText = string.Join("", mentionMsg.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("can you help with this?", mentionText);
    }

    [Fact]
    public async Task Recovered_session_does_not_re_backfill()
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
        var httpClient = new HttpClient(_httpHandler);

        var fetchCount = 0;
        var countingFetcher = new SlackThreadHistoryFetcher(
            (channelId, threadTs, limit, cursor, ct) =>
            {
                Interlocked.Increment(ref fetchCount);
                return FakeRepliesFetcher(channelId, threadTs, limit, cursor, ct);
            },
            new SlackChannelOptions { BotToken = new SensitiveString("xoxb-fake") },
            httpClient,
            new NullContentScanner(),
            NullLogger<SlackThreadHistoryFetcher>.Instance);

        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = true,
                AllowedChannelIds = ["C_RECOVERY"],
                BotToken = new SensitiveString("xoxb-fake-token")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            HttpClient: httpClient,
            ThreadHistoryFetcher: countingFetcher);

        var gateway = Sys.ActorOf(SlackGatewayActor.CreateProps(deps), "slack-gw-recovery");

        // First mention — triggers backfill
        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("C_RECOVERY:4000.1"),
            ChannelId: new SlackChannelId("C_RECOVERY"),
            ThreadTs: new SlackThreadTs("4000.0"),
            EventTs: new SlackEventTs("4000.1"),
            UserId: new SlackUserId("U_USER"),
            BotId: null,
            Text: "<@UBOT> first message",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_chatClient.CallCount >= 1, "Expected first LLM call");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, fetchCount);

        // Second message in the same thread — should NOT re-backfill
        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.AppMention,
            EventId: new SlackEventId("C_RECOVERY:4000.2"),
            ChannelId: new SlackChannelId("C_RECOVERY"),
            ThreadTs: new SlackThreadTs("4000.0"),
            EventTs: new SlackEventTs("4000.2"),
            UserId: new SlackUserId("U_USER"),
            BotId: null,
            Text: "<@UBOT> follow up",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false));

        await AwaitAssertAsync(() =>
        {
            Assert.True(_chatClient.CallCount >= 2, "Expected second LLM call");
        }, duration: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Backfill should only have been called once
        Assert.Equal(1, fetchCount);
    }

    // --- Fake replies fetcher ---

    private Task<SlackNet.WebApi.ConversationMessagesResponse> FakeRepliesFetcher(
        string channelId, string threadTs, int limit, string? cursor, CancellationToken ct)
    {
        return Task.FromResult(new SlackNet.WebApi.ConversationMessagesResponse
        {
            Messages =
            [
                new SlackNet.Events.MessageEvent { Ts = threadTs, User = "U_ROOT", Text = "thread root" },
                new SlackNet.Events.MessageEvent
                {
                    Ts = $"{threadTs[..^1]}1",
                    User = "U_ALICE",
                    Text = "Has anyone looked at the dashboard?",
                    Files =
                    [
                        new SlackNet.File
                        {
                            Id = "F_HIST1",
                            Name = "screenshot.png",
                            Mimetype = "image/png",
                            Size = FakePngBytes.Length,
                            UrlPrivateDownload = "https://files.slack.com/fake/screenshot.png"
                        }
                    ]
                },
                new SlackNet.Events.MessageEvent
                {
                    Ts = $"{threadTs[..^1]}2",
                    User = "U_BOB",
                    Text = "I think it's the new query"
                }
            ]
        });
    }

    // --- Fake helpers (same patterns as SlackFileFlowIntegrationTests) ---

    private sealed class FakeSlackFileHandler : DelegatingHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakePngBytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
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

        public Task UploadFileToThreadAsync(
            SlackChannelId channelId, SlackThreadTs threadTs, string filePath,
            string? filename = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Chat client that captures the full message list sent to the LLM.
    /// </summary>
    private sealed class ContextCapturingChatClient : IChatClient
    {
        private int _callCount;
        public int CallCount => _callCount;
        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastMessages = messages.ToList();

            var response = new ChatResponse(new ChatMessage(
                AiChatRole.Assistant,
                (IList<AIContent>)[new TextContent($"[fake response #{_callCount}]")]));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => StreamAsync(messages, options, cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
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
