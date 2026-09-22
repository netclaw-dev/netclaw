// -----------------------------------------------------------------------
// <copyright file="TelegramGatewayContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Wires the real <see cref="TelegramGatewayActor"/> and the real
/// <see cref="TelegramConversationActor"/>, so the ACL gate and the gateway
/// event dedup stay in the path.
/// <para>
/// <c>TelegramGatewayDependencies</c> has no session-props factory, so the
/// fixture observes a routed message through a pipeline fake that tells the
/// test probe from <c>CreateAsync</c>. The session binding actor initializes
/// the pipeline exactly once per routed message, so "a routed message" and
/// "a pipeline was created for it" are one observable event. A denied message
/// dies at the conversation ACL gate and a duplicate dies at the gateway
/// dedup — neither reaches pipeline creation.
/// </para>
/// </summary>
public sealed class TelegramGatewayContractTests(ITestOutputHelper output)
    : GatewayRoutingContractTests(output)
{
    protected override IActorRef CreateGateway(ChannelOptionsBuilder options)
    {
        // The contract base passes string ids; Telegram ids are numeric. The
        // same map runs on the options allowlists and on every message, so
        // allowlist membership still decides the ACL verdicts.
        var telegramOptions = new TelegramChannelOptions
        {
            Enabled = true,

            // Routing must not be the reason an allowed message fails to reach
            // the session — the mention gate belongs to the routing contract.
            MentionOnly = false,
            AllowDirectMessages = options.AllowDirectMessages,
            AllowedChatIds = options.AllowedChannelIds
                .Select(TelegramContractIds.LongId)
                .Select(id => id.ToString())
                .ToArray(),
            AllowedUserIds = options.AllowedUserIds
                .Select(TelegramContractIds.LongId)
                .Select(id => id.ToString())
                .ToArray()
        };

        var transport = new TelegramTransport(
            telegramOptions,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => throw new NotSupportedException(
                "The gateway contract never starts the Telegram transport; no send path is exercised."));

        var deps = new TelegramGatewayDependencies(
            Pipeline: new GatewaySignalPipeline(TestActor),
            IngressGate: null,
            Options: telegramOptions,
            Transport: transport,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: ToolAudienceProfileDefaults.CreateProfiles(),
            ModelCapabilities: new ModelCapabilities(),
            StorageResolver: Netclaw.Actors.Protocol.TestSessionStorageResolver.Instance,
            ChannelRegistry: null);

        return Sys.ActorOf(TelegramGatewayActor.CreateProps(deps));
    }

    protected override object CreateAllowedMessage(
        string channelId, string threadId, string userId, string text, string eventId)
        => new TelegramInboundMessage(
            ChatId: TelegramContractIds.LongId(channelId),
            UserId: TelegramContractIds.LongId(userId),
            MessageId: TelegramContractIds.IntId(eventId),
            Text: text,
            IsDirectMessage: false,
            ContainsBotMention: false,
            IsReplyToBot: false,
            MessageThreadId: null);

    protected override object CreateDeniedMessage(
        string channelId, string userId, string eventId)
        => new TelegramInboundMessage(
            ChatId: TelegramContractIds.LongId(channelId),
            UserId: TelegramContractIds.LongId(userId),
            MessageId: TelegramContractIds.IntId(eventId),
            Text: "denied",
            IsDirectMessage: false,
            ContainsBotMention: false,
            IsReplyToBot: false,
            MessageThreadId: null);

    private sealed record Routed(string SessionId);

    /// <summary>
    /// A pipeline fake whose whole job is one signal: <c>CreateAsync</c> tells
    /// the test probe. The materialized streams carry nothing, so the fake can
    /// never influence delivery or output behavior.
    /// </summary>
    private sealed class GatewaySignalPipeline : ISessionPipeline
    {
        private readonly IActorRef _probe;

        public GatewaySignalPipeline(IActorRef probe) => _probe = probe;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            _probe.Tell(new Routed(sessionId.Value));

            var killSwitch = KillSwitches.Shared($"gateway-signal-{sessionId.Value}");
            var input = Sink.ForEach<ChannelInput>(_ => { }).ObservingFault();
            var output = Source.Empty<SessionOutput>()
                .Concat(Source.Never<SessionOutput>())
                .Via(killSwitch.Flow<SessionOutput>());

            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}
