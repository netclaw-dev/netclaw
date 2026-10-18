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
        // The stream finishes with Stop. Only then do repeated empty replies blame the model.
        _fakeChatClient.PlannedFinishReason = ChatFinishReason.Stop;
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
        // The model broke a rule the runtime enforces. A provider outage uses ProviderFailure.
        // Asserted by name: the error category contract in HeadlessTurnErrorCategoryTests names ModelBehavior.
        Assert.Equal("ModelBehavior", error.Category.ToString());
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Six main-model calls: 5 Retries + Fail.
        Assert.Equal(6, _fakeChatClient.CallCount);

        // The model never emitted a tool call, so nothing executed.
        Assert.Equal(0, _fakeToolExecutor.CallCount);
    }

    // Production case (sub-agent fea1ce26): "Now the scene file." with 69042 chars of
    // reasoning, no tool call, finishReason=length. The model ran out of output budget
    // before it emitted the tool call, so the text is a stub and not an answer.
    private const string TruncatedStub = "Now the scene file.";

    [Fact]
    public async Task Text_reply_cut_off_by_length_fails_the_turn_and_is_not_posted_as_the_answer()
    {
        _fakeChatClient.PlannedResponses.Enqueue(
        [
            new TextReasoningContent(new string('t', 20_000)),
            new TextContent(TruncatedStub)
        ]);
        _fakeChatClient.PlannedFinishReasons.Enqueue(ChatFinishReason.Length);

        var outputs = await RunTurnAsync("test-channel/truncated-text", "Write the scene file");

        // The stub must not reach the user as a final answer.
        Assert.DoesNotContain(outputs.OfType<TextOutput>(),
            text => text.Text.Contains(TruncatedStub, StringComparison.Ordinal));

        // The user must see why the turn ended.
        var error = Assert.Single(outputs.OfType<ErrorOutput>());
        Assert.Contains("truncat", error.Message, StringComparison.OrdinalIgnoreCase);
        // The provider worked. The model used all of its output budget, so the model gets the blame.
        // Asserted by name: the error category contract in HeadlessTurnErrorCategoryTests names ModelBehavior.
        Assert.Equal("ModelBehavior", error.Category.ToString());

        // Fail fast: no retry call.
        Assert.Equal(1, _fakeChatClient.CallCount);
    }

    [Fact]
    public async Task Text_reply_that_ends_with_stop_completes_normally()
    {
        _fakeChatClient.PlannedResponses.Enqueue(
        [
            new TextReasoningContent("plan the answer"),
            new TextContent("The scene file is ready.")
        ]);
        _fakeChatClient.PlannedFinishReasons.Enqueue(ChatFinishReason.Stop);

        var outputs = await RunTurnAsync("test-channel/stop-text", "Write the scene file");

        Assert.Contains(outputs.OfType<TextOutput>(),
            text => text.Text.Contains("The scene file is ready.", StringComparison.Ordinal));
        Assert.Empty(outputs.OfType<ErrorOutput>());
    }

    [Fact]
    public async Task Tool_call_response_that_ends_with_length_still_runs_the_tool()
    {
        // Today a tool call wins over the finish reason. A cut-off that still carries a
        // complete tool call keeps working; only a reply without a tool call is a failure.
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "scene" })
        ];
        _fakeChatClient.PlannedFinishReasons.Enqueue(ChatFinishReason.Length);
        _fakeToolExecutor.Results["web_search"] = "search result";

        var outputs = await RunTurnAsync("test-channel/length-with-tool", "Find the scene");

        Assert.Equal(1, _fakeToolExecutor.CallCount);
        Assert.Single(outputs.OfType<ToolResultOutput>());
        Assert.Contains(outputs.OfType<TextOutput>(),
            text => text.Text.Contains("fake", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(outputs.OfType<ErrorOutput>());
    }

    [Fact]
    public async Task Thinking_only_reply_cut_off_by_length_still_retries_and_recovers()
    {
        _fakeChatClient.PlannedResponses.Enqueue([new TextReasoningContent("still thinking")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("Recovered answer.")]);
        _fakeChatClient.PlannedFinishReasons.Enqueue(ChatFinishReason.Length);
        _fakeChatClient.PlannedFinishReasons.Enqueue(ChatFinishReason.Stop);

        var outputs = await RunTurnAsync("test-channel/thinking-length-retry", "Answer please");

        Assert.Contains(outputs.OfType<TextOutput>(),
            text => text.Text.Contains("Recovered answer.", StringComparison.Ordinal));
        Assert.Empty(outputs.OfType<ErrorOutput>());
        Assert.Equal(2, _fakeChatClient.CallCount);
    }

    /// <summary>Runs one user turn and returns every output up to and including <see cref="TurnCompleted"/>.</summary>
    private async Task<IReadOnlyList<SessionOutput>> RunTurnAsync(string sessionIdValue, string userText)
    {
        var sessionId = new SessionId(sessionIdValue);
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("turn-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = userText
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var outputs = new List<SessionOutput>();
        while (true)
        {
            var output = await subscriber.ExpectMsgAsync<SessionOutput>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            outputs.Add(output);
            if (output is TurnCompleted)
                return outputs;
        }
    }
}
