// -----------------------------------------------------------------------
// <copyright file="SlackConversationActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackConversationActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Conversation_defers_passivation_while_child_has_pending_approvals()
    {
        var sink = CreateTestProbe("slack-passivation-sink");
        var deps = CreateDependencies(
            threadPropsFactory: (_, _, _, _) => Props.Create(() => new ForwardActor(sink.Ref)));

        var conversation = Sys.ActorOf(
            SlackConversationActor.CreateProps(new SlackChannelId("C1"), deps),
            $"slack-conversation-passivation-{Guid.NewGuid():N}");
        var watcher = CreateTestProbe("slack-passivation-watcher");
        watcher.Watch(conversation);

        conversation.Tell(new PendingApprovalStateChanged(TestActor, 1));
        conversation.Tell(ReceiveTimeout.Instance);

        await watcher.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        conversation.Tell(new PendingApprovalStateChanged(TestActor, 0));
        conversation.Tell(ReceiveTimeout.Instance);

        await watcher.ExpectTerminatedAsync(conversation, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static SlackGatewayDependencies CreateDependencies(
        Func<SlackChannelId, SlackGatewayDependencies, Props>? conversationPropsFactory = null,
        Func<SessionId, SlackChannelId, SlackThreadTs, SlackGatewayDependencies, Props>? threadPropsFactory = null)
    {
        return new SlackGatewayDependencies(
            Pipeline: null!,
            IngressGate: null,
            ActorSystem: null!,
            TimeProvider: TimeProvider.System,
            Options: new SlackChannelOptions
            {
                MentionOnly = true,
                AllowDirectMessages = true,
                AllowedChannelIds = ["C1"]
            },
            BotUserId: new SlackUserId("UBOT"),
            DefaultChannelId: null,
            ReplyClient: new NoopReplyClient(),
            ContentScanner: new NullContentScanner(),
            ThreadHistoryFetcher: EmptyThreadHistoryFetcher.Instance,
            AudienceProfiles: TestSlackGatewayDeps.DefaultAudienceProfiles,
            ModelCapabilities: TestSlackGatewayDeps.DefaultVisionCapableModel,
            Paths: TestSlackGatewayDeps.NewTestPaths(),
            ConversationPropsFactory: conversationPropsFactory,
            ThreadPropsFactory: threadPropsFactory,
            PromptInjectionDetector: SafePromptInjectionDetector.Instance);
    }
}
