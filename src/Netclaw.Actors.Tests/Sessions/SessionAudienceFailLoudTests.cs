// -----------------------------------------------------------------------
// <copyright file="SessionAudienceFailLoudTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// A session with a tool access policy and no trust context has no resolved
/// audience for tool exposure. Before this change the policy treated the
/// missing trust context as Public and exposed the Public tool set. The
/// session now exposes no tools and writes an Error log with the
/// <c>audience_unresolved</c> reason.
/// </summary>
public sealed class SessionToolExposureWithoutTrustContextTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();

    public SessionToolExposureWithoutTrustContextTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
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

        var registry = new ToolRegistry();
        var toolAccessPolicy = TestToolAccessPolicy.Create(new ToolConfig());
        registry.RegisterCore(new SearchToolsTool(registry, toolAccessPolicy));
        registry.RegisterCore(new LoadToolTool(registry, toolAccessPolicy));
        services.AddSingleton(registry);
        services.AddSingleton(toolAccessPolicy);
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        // Deliberately no TrustContextDeriver: this is the defect under test.
    }

    [Fact]
    public async Task Missing_trust_context_exposes_no_tools_and_logs_an_error()
    {
        var sessionId = new SessionId("signalr/no-trust-context");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("no-trust-context-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        await EventFilter.Error(contains: "Tool exposure refused reason=audience_unresolved")
            .ExpectAsync(1, async () =>
            {
                await sessionManager.Ask<CommandAck>(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = "Use the initial tool set",
                    Source = PersonalOperatorSource()
                }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

                await subscriber.ExpectMsgAsync<TextOutput>(
                    TimeSpan.FromSeconds(6),
                    cancellationToken: TestContext.Current.CancellationToken);
                await subscriber.ExpectMsgAsync<TurnCompleted>(
                    TimeSpan.FromSeconds(6),
                    cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);

        // The old behavior sent the Public core tools (load_tool, search_tools).
        var firstCallTools = Assert.Single(_fakeChatClient.ReceivedToolNames);
        Assert.Empty(firstCallTools);
    }

    private static MessageSource PersonalOperatorSource() => new()
    {
        ChannelType = ChannelType.SignalR,
        SenderId = new SenderId("operator-1"),
        ChannelId = "operator-channel",
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        ReceivedAt = DateTimeOffset.UnixEpoch
    };
}

/// <summary>
/// Pins the rule that the session actor reads a turn audience only from the
/// turn authority. Memory checkpoints and routed skill sub-agents use this
/// rule. Before this change both sites replaced a missing authority with
/// Public.
/// </summary>
public sealed class TurnAuthorityAudienceTests
{
    [Fact]
    public void No_turn_context_and_no_turn_source_resolves_no_audience()
        => Assert.Null(LlmSessionActor.ResolveTurnAuthorityAudience(turnContext: null, turnSource: null));

    [Fact]
    public void Turn_context_audience_wins_over_turn_source_audience()
    {
        var context = new TurnContext
        {
            SessionId = new SessionId("signalr/authority"),
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            RequesterPrincipal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
        };

        Assert.Equal(
            TrustAudience.Team,
            LlmSessionActor.ResolveTurnAuthorityAudience(context, Source(TrustAudience.Personal)));
        Assert.Equal(
            TrustAudience.Public,
            LlmSessionActor.ResolveTurnAuthorityAudience(turnContext: null, Source(TrustAudience.Public)));
    }

    private static MessageSource Source(TrustAudience audience) => new()
    {
        ChannelType = ChannelType.SignalR,
        SenderId = new SenderId("operator-1"),
        Audience = audience,
        Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        ReceivedAt = DateTimeOffset.UnixEpoch
    };
}
