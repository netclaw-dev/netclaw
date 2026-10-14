// -----------------------------------------------------------------------
// <copyright file="MaxToolIterationTests.cs" company="Petabridge, LLC">
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
/// Integration tests for productive tool work without a fixed iteration ceiling.
/// </summary>
public class MaxToolIterationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public MaxToolIterationTests(ITestOutputHelper output) : base(output)
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
            MaxToolIterationsPerTurn = 3,
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
        registry.RegisterCore(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Distinct_productive_tool_rounds_continue_past_the_former_limit()
    {
        const int FormerLimit = 3;
        const int UsefulRounds = FormerLimit + 2;
        for (var i = 1; i <= UsefulRounds; i++)
        {
            _fakeChatClient.PlannedResponses.Enqueue([
                new FunctionCallContent($"call-{i}", "web_search",
                    new Dictionary<string, object?> { ["query"] = $"query-{i}" })]);
        }
        _fakeToolExecutor.ExecuteOverride = (call, _) =>
            Task.FromResult($"result:{call.Arguments!["query"]}");

        var sessionId = new SessionId("test-channel/max-iter-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("max-iter-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use each distinct search result to complete the task"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var results = new List<ToolResultOutput>();
        for (var i = 0; i < UsefulRounds; i++)
        {
            await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            results.Add(await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken));
        }

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(UsefulRounds, _fakeToolExecutor.CallCount);
        Assert.Equal(UsefulRounds + 1, _fakeChatClient.CallCount);
        Assert.Equal(Enumerable.Range(1, UsefulRounds).Select(static round => $"result:query-{round}"),
            results.Select(static result => result.Result));
        Assert.All(_fakeChatClient.ReceivedToolNames.Take(UsefulRounds), names => Assert.Contains("web_search", names));
    }
}
