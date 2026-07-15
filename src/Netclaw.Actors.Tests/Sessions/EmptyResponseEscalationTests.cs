// -----------------------------------------------------------------------
// <copyright file="EmptyResponseEscalationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Verifies the actor-level wiring of the per-turn empty/thinking-only response
/// bound (issue #1346): when the model produces consecutive thinking-only
/// responses without doing tool work, the session retries with nudges up to the
/// consecutive limit then fails the turn.
/// </summary>
public class EmptyResponseEscalationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public EmptyResponseEscalationTests(ITestOutputHelper output) : base(output)
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
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Repeated_thinking_only_responses_fail_turn_after_consecutive_limit()
    {
        // The model never produces a reply — only reasoning. Pre-tool consecutive
        // limit is 5 retries, so the sequence is: 5 Retries then Fail on the 6th.
        for (var i = 0; i < 6; i++)
            _fakeChatClient.PlannedResponses.Enqueue(
                [new TextReasoningContent($"[fake thinking] still pondering #{i}...")]);

        var sessionId = new SessionId("test-channel/empty-escalation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();

        var subscriber = CreateTestProbe("empty-escalation-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.None
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Please answer"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Six main-model calls: 5 Retries + Fail.
        Assert.Equal(6, _fakeChatClient.CallCount);

        // The model never emitted a tool call, so nothing executed.
        Assert.Equal(0, _fakeToolExecutor.CallCount);
    }
}
