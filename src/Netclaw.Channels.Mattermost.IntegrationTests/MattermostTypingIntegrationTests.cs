// -----------------------------------------------------------------------
// <copyright file="MattermostTypingIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using Mattermost;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost.Transport;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Mattermost.IntegrationTests;

/// <summary>
/// Proves that a separate Mattermost user receives the native typing events
/// that the session binding sends for an active session (issue #2347).
/// </summary>
[Collection("Mattermost")]
public sealed class MattermostTypingIntegrationTests(
    MattermostFixture fixture, ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TestSessionTempDirectory _state = TestSessionTempDirectory.Create(
        prefix: "netclaw-mattermost-typing-", createDirectoryTree: true);

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    protected override async Task AfterAllAsync()
    {
        try
        {
            await base.AfterAllAsync();
        }
        finally
        {
            await _state.DisposeAsync();
        }
    }

    [Fact]
    public async Task Active_session_sends_repeat_typing_events_to_a_separate_user()
    {
        fixture.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var rootId = await fixture.PostAsTestUserAsync(fixture.ChannelId, "Typing indicator thread root");
        var sessionId = new SessionId($"{fixture.ChannelId}/{rootId}");

        // The observer is the test user, not the bot. Mattermost does not send
        // a typing event back to the user that typed.
        var (testUserHttp, testUserToken) = await fixture.CreateTestUserClientAsync();
        testUserHttp.Dispose();
        using var observerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var observer = new ClientWebSocket();
        observer.Options.SetRequestHeader("Authorization", $"Bearer {testUserToken}");
        var socketUri = new UriBuilder(fixture.ServerUrl) { Scheme = "ws", Path = "/api/v4/websocket" }.Uri;
        await observer.ConnectAsync(socketUri, ct);

        var typingEvents = Channel.CreateUnbounded<TypingEvent>();
        var authenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveLoop = ReceiveEventsAsync(observer, authenticated, typingEvents.Writer, observerCts.Token);
        // The server sends "hello" after it authenticates the socket. A pulse
        // that the bot sends before that point does not reach the observer.
        await authenticated.Task.WaitAsync(Timeout, ct);

        try
        {
            using var bot = new MattermostClient(fixture.ServerUrl, fixture.BotToken);
            var replyClient = new MattermostNetReplyClient(bot);
            var gateway = Sys.ActorOf(MattermostGatewayActor.CreateProps(new MattermostGatewayDependencies(
                Pipeline: new ProcessingSessionPipeline(), IngressGate: null, TimeProvider: TimeProvider.System,
                Options: new MattermostChannelOptions
                {
                    MentionOnly = true, AllowedChannelIds = [fixture.ChannelId], AllowedUserIds = [fixture.TestUserId]
                },
                DefaultChannelId: null,
                ChannelRegistry: MattermostIntegrationRegistries.WithProcessingRenderer(replyClient),
                ReplyClient: replyClient,
                ContentScanner: new NullContentScanner(), AudienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
                ModelCapabilities: new ModelCapabilities { ModelId = "test", InputModalities = ModelModality.Text },
                StorageResolver: new TestSessionStorageResolver(_state.Paths),
                PromptInjectionDetector: new RegexPromptInjectionDetector(NullLogger<RegexPromptInjectionDetector>.Instance))),
                "mattermost-typing");

            // The proactive thread starts the session binding. Its pipeline
            // reports an active session and never reports idle.
            await gateway.Ask<MattermostProactiveThreadAck>(new StartMattermostProactiveThread(
                new MattermostChannelId(fixture.ChannelId), new MattermostRootPostId(rootId), sessionId), Timeout, ct);

            // The second event is a repeat pulse from the binding timer.
            for (var pulse = 0; pulse < 2; pulse++)
            {
                var typing = await ReadThreadTypingEventAsync(typingEvents.Reader, rootId, ct);
                Assert.Equal(fixture.BotUserId, typing.UserId);
                Assert.Equal(fixture.ChannelId, typing.ChannelId);
            }
        }
        finally
        {
            await observerCts.CancelAsync();
            // The cancellation stops the receive loop. A loop fault already
            // reached the test through the typing event channel.
            await receiveLoop.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>
    /// Reads raw WebSocket events. The Mattermost.NET event callback drops the
    /// <c>broadcast</c> object, and only that object has the channel ID of a
    /// typing event.
    /// </summary>
    private static async Task ReceiveEventsAsync(
        ClientWebSocket socket,
        TaskCompletionSource authenticated,
        ChannelWriter<TypingEvent> typingEvents,
        CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("The Mattermost server closed the observer WebSocket.");

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);

                if (!doc.RootElement.TryGetProperty("event", out var eventName))
                    continue;

                switch (eventName.GetString())
                {
                    case "hello":
                        authenticated.TrySetResult();
                        break;
                    case "typing":
                        typingEvents.TryWrite(TypingEvent.Parse(doc.RootElement));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // A waiting test must fail with the cause, not with a timeout.
            authenticated.TrySetException(ex);
            typingEvents.TryComplete(ex);
            throw;
        }
    }

    private static async Task<TypingEvent> ReadThreadTypingEventAsync(
        ChannelReader<TypingEvent> reader,
        string rootId,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        // Other test classes share the server and the channel. Skip a typing
        // event that belongs to a different thread.
        while (true)
        {
            var typing = await reader.ReadAsync(timeout.Token);
            if (typing.ParentId == rootId)
                return typing;
        }
    }

    private sealed record TypingEvent(string? UserId, string? ChannelId, string? ParentId)
    {
        public static TypingEvent Parse(JsonElement root)
        {
            var data = root.GetProperty("data");
            var broadcast = root.GetProperty("broadcast");
            return new TypingEvent(
                data.GetProperty("user_id").GetString(),
                broadcast.GetProperty("channel_id").GetString(),
                data.GetProperty("parent_id").GetString());
        }
    }

    /// <summary>
    /// Model substitute that reports an active session when the pipeline
    /// starts. It never reports idle, so the binding repeats the pulse.
    /// </summary>
    private sealed class ProcessingSessionPipeline : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(SessionId sessionId, SessionPipelineOptions options,
            IMaterializer? materializer = null, CancellationToken cancellationToken = default)
        {
            var killSwitch = KillSwitches.Shared("typing-proof");
            var output = Source.Single<SessionOutput>(new ProcessingStateOutput(true) { SessionId = sessionId })
                .Concat(Source.Never<SessionOutput>())
                .Via(killSwitch.Flow<SessionOutput>());
            var input = Sink.Ignore<ChannelInput>().MapMaterializedValue(_ => NotUsed.Instance);
            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}

/// <summary>
/// Channel registries for the integration tests that start the real gateway.
/// The descriptor matches the daemon registration for Mattermost.
/// </summary>
internal static class MattermostIntegrationRegistries
{
    public static IChannelRegistry WithProcessingRenderer(IMattermostReplyClient replyClient)
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);
        var descriptor = new ChannelDescriptor(
            key,
            ChannelType.Mattermost,
            ChannelKind.RemoteChat,
            "Mattermost",
            IsEnabled: true,
            ChannelCapabilities.ReceiveMessages
                | ChannelCapabilities.SendMessages
                | ChannelCapabilities.ThreadedConversations
                | ChannelCapabilities.InteractiveApproval,
            ToolIntents: new HashSet<ChannelToolIntentKind> { ChannelToolIntentKind.SendMessage },
            AddressKinds: new HashSet<ChannelAddressKind>
            {
                ChannelAddressKind.Destination,
                ChannelAddressKind.Thread
            },
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.TextMessage,
                ChannelOutputEffectKind.InteractiveApproval,
                ChannelOutputEffectKind.ProcessingIndicator
            });

        return new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            [],
            outputRenderers: [new MattermostProcessingOutputRenderer(replyClient)]);
    }
}
