// -----------------------------------------------------------------------
// <copyright file="TelegramSessionTopicsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramSessionTopicsTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task One_chat_with_two_topics_produces_two_sessions_and_threaded_replies()
    {
        var client = new TelegramTransportTests.FakeTelegramBotApiClient();
        var pipeline = new PerSessionPipeline(sessionId => [new TextOutput("reply") { SessionId = sessionId }]);

        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token"),
            AllowedChatIds = ["77"],
        };
        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => client);
        await transport.StartAsync(TestContext.Current.CancellationToken);

        var dependencies = new TelegramGatewayDependencies(
            pipeline,
            IngressGate: null,
            options,
            transport,
            ContentScanner: null!,
            new ToolAudienceProfiles(),
            new ModelCapabilities(),
            TestSessionStorageResolver.Instance,
            ConversationPropsFactory: null);
        var gateway = Sys.ActorOf(TelegramGatewayActor.CreateProps(dependencies), "topics-gateway");

        gateway.Tell(new TelegramInboundMessage(
            77, 1, 1001, "hello from topic 101", IsDirectMessage: false,
            ContainsBotMention: true, MessageThreadId: 101), ActorRefs.NoSender);

        await AwaitAssertAsync(
            () => Assert.Single(client.SentTexts),
            cancellationToken: TestContext.Current.CancellationToken);

        var topic101 = pipeline.Find("77/101");
        Assert.NotNull(topic101);
        Assert.Equal(101, client.SentTexts[0].MessageThreadId);

        gateway.Tell(new TelegramInboundMessage(
            77, 1, 1002, "hello from topic 102", IsDirectMessage: false,
            ContainsBotMention: true, MessageThreadId: 102), ActorRefs.NoSender);

        await AwaitAssertAsync(
            () => Assert.Equal(2, client.SentTexts.Count),
            cancellationToken: TestContext.Current.CancellationToken);

        var topic102 = pipeline.Find("77/102");
        Assert.NotNull(topic102);
        Assert.NotEqual(topic101, topic102);
        Assert.Equal(102, client.SentTexts[1].MessageThreadId);

        gateway.Tell(new TelegramInboundMessage(
            77, 1, 1003, "hello again from topic 101", IsDirectMessage: false,
            ContainsBotMention: true, MessageThreadId: 101), ActorRefs.NoSender);

        // The reused topic must not open a third session, and its message must
        // land in session 77/101's pipeline again.
        await AwaitAssertAsync(
            () =>
            {
                Assert.Equal(2, client.SentTexts.Count);
                Assert.Equal(2, topic101.CapturedInputs.Count);
                Assert.Single(topic102.CapturedInputs);
                Assert.Equal(2, pipeline.SessionCount);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        gateway.Tell(new TelegramInboundMessage(
            77, 1, 1004, "ordinary message without a topic", IsDirectMessage: false,
            ContainsBotMention: true), ActorRefs.NoSender);

        await AwaitAssertAsync(
            () => Assert.Equal(3, client.SentTexts.Count),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, pipeline.SessionCount);
        Assert.NotNull(pipeline.Find("77/chat"));
        Assert.Null(client.SentTexts[2].MessageThreadId);
    }

    [Theory]
    [InlineData(101, "101")]
    [InlineData(null, "chat")]
    public void ThreadKey_maps_topic_ids_and_keeps_the_chat_fallback(int? messageThreadId, string expected) =>
        Assert.Equal(expected, TelegramConversationActor.ThreadKeyFor(messageThreadId));

    /// <summary>
    /// Routes pipeline creation to one <see cref="RecordingSessionPipeline"/>
    /// per session id, so a test can observe each session's inputs separately.
    /// </summary>
    private sealed class PerSessionPipeline(
        Func<SessionId, IReadOnlyList<SessionOutput>> outputFactory) : ISessionPipeline
    {
        private readonly ConcurrentDictionary<string, RecordingSessionPipeline> _pipelines =
            new(StringComparer.Ordinal);

        public int SessionCount => _pipelines.Count;

        public RecordingSessionPipeline? Find(string sessionId) =>
            _pipelines.TryGetValue(sessionId, out var pipeline) ? pipeline : null;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            var pipeline = _pipelines.GetOrAdd(
                sessionId.Value,
                _ => new RecordingSessionPipeline(outputFactory, reactive: true));
            return pipeline.CreateAsync(sessionId, options, materializer, cancellationToken);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Find(feedback.SessionId.Value)?.SendFeedbackAsync(feedback, ct) ?? Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Find(feedback.SessionId.Value)?.SendFeedbackAndWaitAsync(feedback, ct)
            ?? Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}
