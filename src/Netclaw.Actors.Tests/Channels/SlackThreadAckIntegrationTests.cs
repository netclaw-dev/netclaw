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
using Netclaw.Actors.Tools;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackThreadAckIntegrationTests(ITestOutputHelper output) : TestKit(output: output)
{
    private readonly ToolLoopChatClient _chatClient = new();
    private readonly FakeToolExecutor _toolExecutor = new();
    private readonly RecordingReplyClient _replyClient = new();

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new SessionConfig
        {
            ModelId = "slack-ack-test-model",
            ContextWindowTokens = 128_000,
            SnapshotInterval = 5,
            MaxToolIterationsPerTurn = 10,
            TitleGenerationInterval = 0
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(_toolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
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
    public async Task Hidden_tool_activity_posts_single_ack_before_final_reply()
    {
        _chatClient.ToolCallsBeforeText = 3;
        _toolExecutor.Results["web_search"] = "search result";

        var gateway = CreateGateway("slack-gw-ack-threshold-test");

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D7:7000"),
            ChannelId: new SlackChannelId("D7"),
            ThreadTs: null,
            EventTs: new SlackEventTs("7000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "do the work",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: null));

        await AwaitAssertAsync(() => Assert.Equal(2, _replyClient.PostedMessages.Count), TimeSpan.FromSeconds(10));

        Assert.Equal("Working on it.", _replyClient.PostedMessages[0].Text);
        Assert.Contains("[final]", _replyClient.PostedMessages[1].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fast_turn_stays_quiet_until_final_reply()
    {
        _replyClient.PostedMessages.Clear();
        _chatClient.ToolCallsBeforeText = 2;
        _toolExecutor.Results["web_search"] = "search result";

        var gateway = CreateGateway("slack-gw-fast-turn-test");

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D8:8000"),
            ChannelId: new SlackChannelId("D8"),
            ThreadTs: null,
            EventTs: new SlackEventTs("8000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "quick research",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: null));

        await AwaitAssertAsync(() => Assert.Single(_replyClient.PostedMessages), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(_replyClient.PostedMessages, x => string.Equals(x.Text, "Working on it.", StringComparison.Ordinal));
        Assert.Contains("[final]", _replyClient.PostedMessages[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Streamed_reply_does_not_post_generic_fallback_after_real_reply()
    {
        _replyClient.PostedMessages.Clear();
        _chatClient.StreamTextDeltas = true;

        var gateway = CreateGateway("slack-gw-streamed-reply-test");

        gateway.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("D9:9000"),
            ChannelId: new SlackChannelId("D9"),
            ThreadTs: null,
            EventTs: new SlackEventTs("9000.1"),
            UserId: new SlackUserId("U_HUMAN"),
            BotId: null,
            Text: "stream a reply",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: true,
            Files: null));

        await AwaitAssertAsync(() => Assert.Single(_replyClient.PostedMessages), TimeSpan.FromSeconds(10));
        Assert.Equal("Hello world", _replyClient.PostedMessages[0].Text);
        Assert.DoesNotContain("didn't manage to produce a reply", _replyClient.PostedMessages[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    private IActorRef CreateGateway(string name)
    {
        var pipeline = Host.Services.GetRequiredService<SessionPipeline>();
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
            ContentScanner: new NullContentScanner());

        return Sys.ActorOf(SlackGatewayActor.CreateProps(deps), name);
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
            SlackChannelId channelId,
            SlackThreadTs threadTs,
            string filePath,
            string? filename = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ToolLoopChatClient : IChatClient
    {
        private int _callCount;

        public int ToolCallsBeforeText { get; set; }

        public bool StreamTextDeltas { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _callCount);
            var hasTools = options?.Tools?.Count > 0;

            if (!StreamTextDeltas && hasTools && callNumber <= ToolCallsBeforeText)
            {
                var toolCall = new FunctionCallContent(
                    $"call-{callNumber}",
                    "web_search",
                    new Dictionary<string, object?> { ["query"] = $"query-{callNumber}" });
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    [toolCall])));
            }

            var text = StreamTextDeltas ? "Hello world" : $"[final] call #{callNumber}";
            return Task.FromResult(new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [new TextContent(text)])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (StreamTextDeltas)
            {
                yield return new ChatResponseUpdate { Contents = [new TextContent("Hello")] };
                yield return new ChatResponseUpdate { Contents = [new TextContent(" world")] };
                yield break;
            }

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
}
