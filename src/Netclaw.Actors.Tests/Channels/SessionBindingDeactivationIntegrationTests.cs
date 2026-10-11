// -----------------------------------------------------------------------
// <copyright file="SessionBindingDeactivationIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SessionBindingDeactivationIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();

    public SessionBindingDeactivationIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new TestChatClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000
        });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.FromHours(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
    }

    [Fact]
    public async Task Slack_binding_stops_after_its_real_session_passivates()
    {
        var channelId = new SlackChannelId("C-TEST");
        var threadTs = new SlackThreadTs("1000.1");
        var sessionId = SessionIdFormat.Build(channelId.Value, threadTs.Value);
        var replyClient = new RecordingSlackReplyClient();
        var sessionObserver = CreateTestProbe("slack-session-output");
        await JoinSessionAsync(
            ActorRegistry.Get<SessionManagerActorKey>(),
            sessionObserver,
            sessionId,
            OutputFilter.Full);
        var dependencies = new SlackGatewayDependencies(
            Pipeline: Host.Services.GetRequiredService<ISessionPipeline>(),
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = [channelId.Value],
                AllowDirectMessages = true,
                BotToken = new SensitiveString("xoxb-test")
            },
            BotUserId: new SlackUserId("U-BOT"),
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.SlackWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultTextOnlyModel,
            StorageResolver: new TestSessionStorageResolver(TestPaths),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var parent = Sys.ActorOf(SlackConversationActor.CreateProps(channelId, dependencies));
        var bindingName = Uri.EscapeDataString(threadTs.Value);
        parent.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("slack-deactivation-event"),
            ChannelId: channelId,
            ThreadTs: threadTs,
            EventTs: new SlackEventTs("1000.2"),
            UserId: new SlackUserId("U-USER"),
            BotId: null,
            Text: "Reply before session shutdown",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false), TestActor);

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(replyClient.Posts, post => post.Text.Contains(
                "Response #1", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);
        await sessionObserver.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        var binding = await Sys.ActorSelection($"{parent.Path}/{bindingName}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var bindingWatcher = CreateTestProbe("slack-binding-watch");
        bindingWatcher.Watch(binding);
        await AssertBindingSurvivesReceiveTimeoutAsync(binding);

        var parentWatcher = CreateTestProbe("slack-parent-watch");
        parentWatcher.Watch(parent);
        parent.Tell(ReceiveTimeout.Instance);
        parent.Tell(new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("slack-after-parent-timeout"),
            ChannelId: channelId,
            ThreadTs: threadTs,
            EventTs: new SlackEventTs("1000.3"),
            UserId: new SlackUserId("U-USER"),
            BotId: null,
            Text: "Reply after parent timeout",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: false), TestActor);
        await sessionObserver.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);

        var session = await ResolveSessionAsync(sessionId);
        var sessionWatcher = CreateTestProbe("slack-session-watch");
        sessionWatcher.Watch(session);
        session.Tell(ReceiveTimeout.Instance);

        await sessionObserver.ExpectMsgAsync<SessionDeactivated>(
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await sessionWatcher.ExpectTerminatedAsync(
            session,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await bindingWatcher.ExpectTerminatedAsync(
            binding,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await parentWatcher.ExpectTerminatedAsync(
            parent,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Discord_binding_stops_after_its_real_session_passivates()
    {
        var channelId = new DiscordChannelId("CH-TEST");
        var replyChannelId = new DiscordReplyChannelId("REPLY-TEST");
        var threadId = new DiscordThreadOrMessageId("THREAD-TEST");
        var sessionId = SessionIdFormat.Build(channelId.Value, threadId.Value);
        var replyClient = new SignalDiscordReplyClient();
        var sessionObserver = CreateTestProbe("discord-session-output");
        await JoinSessionAsync(
            ActorRegistry.Get<SessionManagerActorKey>(),
            sessionObserver,
            sessionId,
            OutputFilter.Full);
        var dependencies = new DiscordGatewayDependencies(
            Pipeline: Host.Services.GetRequiredService<ISessionPipeline>(),
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: new DiscordChannelOptions
            {
                Enabled = true,
                MentionOnly = false,
                AllowedChannelIds = [channelId.Value]
            },
            DefaultChannelId: null,
            ChannelRegistry: TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            ReplyClient: replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            StorageResolver: new TestSessionStorageResolver(TestPaths),
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
        var parent = Sys.ActorOf(DiscordConversationActor.CreateProps(channelId, dependencies));
        var bindingName = Uri.EscapeDataString($"{channelId.Value}:{threadId.Value}");
        parent.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("discord-deactivation-event"),
            ChannelId: channelId,
            ReplyChannelId: replyChannelId,
            MessageId: new DiscordMessageId("discord-deactivation-message"),
            ThreadOrMessageId: threadId,
            RootMessageId: null,
            SenderId: new DiscordUserId("USER-TEST"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: "Reply before session shutdown",
            ReceivedAt: TimeProvider.System.GetUtcNow()), TestActor);

        var postedReply = await replyClient.FirstPost.Task.WaitAsync(
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Contains("Response #1", postedReply.Text, StringComparison.Ordinal);
        await sessionObserver.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        var binding = await Sys.ActorSelection($"{parent.Path}/{bindingName}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var bindingWatcher = CreateTestProbe("discord-binding-watch");
        bindingWatcher.Watch(binding);
        await AssertBindingSurvivesReceiveTimeoutAsync(binding);

        var parentWatcher = CreateTestProbe("discord-parent-watch");
        parentWatcher.Watch(parent);
        parent.Tell(ReceiveTimeout.Instance);
        parent.Tell(new DiscordGatewayMessage(
            EventId: new DiscordEventId("discord-after-parent-timeout"),
            ChannelId: channelId,
            ReplyChannelId: replyChannelId,
            MessageId: new DiscordMessageId("discord-after-parent-timeout"),
            ThreadOrMessageId: threadId,
            RootMessageId: null,
            SenderId: new DiscordUserId("USER-TEST"),
            IsBotMessage: false,
            IsDirectMessage: false,
            ContainsBotMention: false,
            Text: "Reply after parent timeout",
            ReceivedAt: TimeProvider.System.GetUtcNow()), TestActor);
        await sessionObserver.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);

        var session = await ResolveSessionAsync(sessionId);
        var sessionWatcher = CreateTestProbe("discord-session-watch");
        sessionWatcher.Watch(session);
        session.Tell(ReceiveTimeout.Instance);

        await sessionObserver.ExpectMsgAsync<SessionDeactivated>(
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await sessionWatcher.ExpectTerminatedAsync(
            session,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await bindingWatcher.ExpectTerminatedAsync(
            binding,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
        await parentWatcher.ExpectTerminatedAsync(
            parent,
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        return await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    private async Task AssertBindingSurvivesReceiveTimeoutAsync(IActorRef binding)
    {
        var timeoutProbe = CreateTestProbe("binding-timeout-probe");
        binding.Tell(ReceiveTimeout.Instance, timeoutProbe.Ref);
        binding.Tell(new Identify("binding-survives-timeout"), timeoutProbe.Ref);
        var identity = await timeoutProbe.ExpectMsgAsync<ActorIdentity>(
            TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(binding, identity.Subject);
    }

    private sealed class TestChatClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }

    private sealed class SignalDiscordReplyClient : IDiscordReplyClient
    {
        public TaskCompletionSource<DiscordPostMessage> FirstPost { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiscordPostResult> PostReplyAsync(
            DiscordPostMessage message,
            CancellationToken cancellationToken = default)
        {
            FirstPost.TrySetResult(message);
            return Task.FromResult(new DiscordPostResult(MessageId: new DiscordMessageId("message-test")));
        }

        public Task SetThreadNameAsync(
            DiscordReplyChannelId threadChannelId,
            string name,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateMessageAsync(
            DiscordReplyChannelId channelId,
            DiscordMessageId messageId,
            string text,
            bool removeComponents = false,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TriggerTypingAsync(
            DiscordReplyChannelId channelId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DiscordMessageId?> UploadFileAsync(
            DiscordFileUpload upload,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DiscordMessageId?>(null);
    }
}
