using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class SlackSessionBindingContractTests(ITestOutputHelper output)
    : SessionBindingContractTests(output)
{
    private RecordingSlackReplyClient _replyClient = new();
    private int _actorCounter;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore();
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
        return CreateActorCore(sessionId, pipeline, detector, nameSuffix: "fail");
    }

    protected override object CreateInboundMessage(string text, string senderId)
        => new SlackThreadInbound(
            SessionId: new SessionId("ignored"),
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            EventId: new SlackEventId($"evt-{Guid.NewGuid():N}"),
            TurnId: Guid.NewGuid().ToString("N"),
            SenderId: senderId,
            Audience: TrustAudience.Team,
            Principal: PrincipalClassification.UntrustedExternal,
            Provenance: new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                SourceKind = "slack"
            },
            Text: text,
            ReceivedAt: TimeProvider.System.GetUtcNow());

    protected override object CreateApprovalResponse(string callId, string selectedKey, string senderId)
        => new SlackApprovalResponse(
            ChannelId: new SlackChannelId("C-test"),
            ThreadTs: new SlackThreadTs("1000.1"),
            CallId: callId,
            SelectedKey: selectedKey,
            SenderId: senderId);

    protected override IReadOnlyList<string> GetPostedTexts()
        => _replyClient.Posts.Select(p => p.Text).ToList();

    protected override void ClearPostedTexts()
        => _replyClient.Clear();

    protected override void SetReplyClientThrows(Exception ex)
        => _replyClient.ThrowOnPost = ex;

    protected override void ClearReplyClientThrows()
        => _replyClient.ThrowOnPost = null;

    protected override ChannelType ExpectedChannelType => ChannelType.Slack;

    protected override bool SupportsThreadHydration => true;

    private int _hydrationEventCounter;

    protected override object CreateHydrationTriggerInboundMessage(string text, string senderId)
        => ((SlackThreadInbound)CreateInboundMessage(text, senderId)) with
        {
            EventId = new SlackEventId($"C-test:{1000 + Interlocked.Increment(ref _hydrationEventCounter)}.1")
        };

    protected override IActorRef CreateBindingActorWithHydration(
        SessionId sessionId,
        RecordingSessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        IThreadHistoryFetcher historyFetcher)
    {
        ResetReplyClient();
        return CreateActorCore(sessionId, pipeline, detector, historyFetcher: historyFetcher);
    }

    protected override IReadOnlyList<ChannelInput> CreateHistoryItems(int count)
    {
        var items = new List<ChannelInput>();
        for (var i = 0; i < count; i++)
        {
            items.Add(new ChannelInput
            {
                SenderId = $"user-history-{i}",
                ChannelId = "C-test",
                MessageId = $"C-test:{900 + i}.1",
                Contents = [new TextContent($"history message {i}")],
                ReceivedAt = TimeProvider.System.GetUtcNow().AddMinutes(-(count - i))
            });
        }
        return items;
    }

    private void ResetReplyClient()
    {
        var pendingThrow = _replyClient.ThrowOnPost;
        _replyClient = new RecordingSlackReplyClient { ThrowOnPost = pendingThrow };
    }

    private IActorRef CreateActorCore(
        SessionId sessionId,
        ISessionPipeline pipeline,
        ConfigurablePromptInjectionDetector detector,
        string nameSuffix = "",
        IThreadHistoryFetcher? historyFetcher = null)
    {
        var paths = TestSlackGatewayDeps.NewTestPaths();
        var deps = new SlackGatewayDependencies(
            Pipeline: pipeline,
            IngressGate: null,
            ActorSystem: Sys,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                Enabled = true,
                AllowDirectMessages = true,
                BotToken = new SensitiveString("xoxb-fake")
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: _replyClient,
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: historyFetcher ?? EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultTextOnlyModel,
            Paths: paths,
            PromptInjectionDetector: detector);

        var suffix = string.IsNullOrEmpty(nameSuffix) ? "" : $"-{nameSuffix}";
        var name = $"slack-thread-contract{suffix}-{Interlocked.Increment(ref _actorCounter)}";
        return Sys.ActorOf(SlackThreadBindingActor.CreateProps(
            sessionId,
            new SlackChannelId("C-test"),
            new SlackThreadTs("1000.1"),
            deps), name);
    }
}
