// -----------------------------------------------------------------------
// <copyright file="DiscordSessionBindingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingDiscordReplyClient _replyClient = new();

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    protected override IActorRef CreateBindingActor(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
    {
        ResetReplyClient();
        return CreateActorCore(sessionId, pipeline, detector);
    }

    protected override IActorRef CreateBindingActorWithPipeline(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector)
    {
        ResetReplyClient();
        var options = new DiscordChannelOptions();
        var deps = new DiscordGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: detector);

        return Sys.ActorOf(DiscordSessionBindingActor.CreateProps(
            sessionId,
            new DiscordChannelId("ch-test"),
            new DiscordReplyChannelId("reply-test"),
            new DiscordThreadOrMessageId("thread-test"),
            rootMessageId: null,
            deps));
    }

    protected override object CreateInboundMessage(string text, string senderId)
        => new DiscordThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new DiscordChannelId("ch-test"),
            ReplyChannelId: new DiscordReplyChannelId("reply-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            RootMessageId: null,
            EventId: new DiscordEventId($"evt-{Guid.NewGuid():N}"),
            SenderId: new DiscordUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                SourceKind = "discord"
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateApprovalResponse(string callId, string selectedKey, string senderId)
        => new DiscordApprovalResponse(
            ChannelId: new DiscordChannelId("ch-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            CallId: callId,
            SelectedKey: selectedKey,
            SenderId: new DiscordUserId(senderId));

    protected override IReadOnlyList<string> GetPostedTexts()
        => _replyClient.Posts.Select(p => p.Text).ToList();

    protected override void ClearPostedTexts()
        => _replyClient.Posts.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Discord;

    protected override bool SupportsThreadHydration => true;

    protected override IActorRef CreateBindingActorWithHydration(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher historyFetcher)
    {
        ResetReplyClient();
        return CreateActorCore(sessionId, pipeline, detector, historyFetcher);
    }

    protected override IReadOnlyList<ChannelInput> CreateHistoryItems(int count)
    {
        var items = new List<ChannelInput>();
        for (var i = 0; i < count; i++)
        {
            items.Add(new ChannelInput
            {
                SenderId = $"history-user-{i}",
                ChannelId = "ch-test",
                MessageId = (900_000_000_000_000_000UL + (ulong)i).ToString(),
                Contents = [new Microsoft.Extensions.AI.TextContent($"history message {i}")],
                ReceivedAt = TimeProvider.System.GetUtcNow().AddMinutes(-count + i)
            });
        }

        return items;
    }

    private long _hydrationEventCounter;

    protected override object CreateHydrationTriggerInboundMessage(string text, string senderId)
    {
        var snowflake = 1_000_000_000_000_000_000UL + (ulong)Interlocked.Increment(ref _hydrationEventCounter);
        return new DiscordThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new DiscordChannelId("ch-test"),
            ReplyChannelId: new DiscordReplyChannelId("reply-test"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-test"),
            RootMessageId: null,
            EventId: new DiscordEventId(snowflake.ToString()),
            SenderId: new DiscordUserId(senderId),
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                SourceKind = "discord"
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());
    }

    private void ResetReplyClient()
    {
        var pendingThrow = _replyClient.ThrowOnPost;
        _replyClient = new RecordingDiscordReplyClient { ThrowOnPost = pendingThrow };
    }

    private IActorRef CreateActorCore(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher? historyFetcher = null)
    {
        var options = new DiscordChannelOptions();
        var deps = new DiscordGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            TimeProvider: TimeProvider.System,
            Options: options,
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            AudienceProfiles: TestDiscordGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestDiscordGatewayDeps.DefaultVisionCapableModel,
            Paths: TestDiscordGatewayDeps.NewTestPaths(),
            PromptInjectionDetector: detector,
            ThreadHistoryFetcher: historyFetcher);

        return Sys.ActorOf(DiscordSessionBindingActor.CreateProps(
            sessionId,
            new DiscordChannelId("ch-test"),
            new DiscordReplyChannelId("reply-test"),
            new DiscordThreadOrMessageId("thread-test"),
            rootMessageId: null,
            deps));
    }
}
