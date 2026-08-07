// -----------------------------------------------------------------------
// <copyright file="ToolBatchHistoryWedgeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Regression test for the tool-batch history wedge.
///
/// When a parallel tool batch partially fails, <see cref="LlmSessionActor"/>
/// appends the "I encountered an error executing a tool" assistant reply
/// (FailCurrentTurn -> SessionState.AddErrorReply) WITHOUT first writing a
/// tool-result for the unanswered call(s). History becomes
/// [assistant tool_calls(A,B), tool result A, assistant error] with B
/// unanswered, and the failed call's synthetic result only lands on a later
/// turn — after the error reply. That violates the invariant strict
/// OpenAI-compatible providers (DeepSeek, Qwen, vLLM) enforce: an assistant
/// message bearing tool_calls MUST be immediately followed by a contiguous run
/// of tool-result messages answering every one of its tool_call ids. The
/// violation makes every subsequent request fail with HTTP 400
/// "insufficient tool messages following tool_calls", wedging the session.
///
/// This test drives a two-call parallel batch where one call fails during
/// interpret (the only pre-try seam that reaches ToolExecutionFailed, so the
/// healthy call is recorded first) and asserts the contiguity invariant on the
/// history assembled for the next provider request. It fails until the actor
/// closes out the unanswered calls before appending the error reply.
/// </summary>
public class ToolBatchHistoryWedgeTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly PartialFailureToolExecutor _executor = new();

    public ToolBatchHistoryWedgeTests(ITestOutputHelper output) : base(output)
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
                MaxInlineToolResultChars = 120,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_executor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Partial_failure_of_parallel_tool_batch_leaves_history_well_formed_for_strict_providers()
    {
        var ct = TestContext.Current.CancellationToken;

        // Turn 1: two parallel tool calls. call-A executes normally; call-B
        // throws in InterpretToolCall, which escapes to ToolExecutionFailed ->
        // FailCurrentTurn. call-A is recorded before the failure (Task.WhenAll
        // invariant), so the batch fails with A answered and B unanswered.
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-A", "web_search",
                new Dictionary<string, object?> { ["query"] = "a" }),
            new FunctionCallContent("call-B", "web_search",
                new Dictionary<string, object?> { ["query"] = "b" }),
        ];
        _executor.FailInterpretForCallIds.Add("call-B");

        var sessionId = new SessionId("test-channel/tool-batch-wedge");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("wedge-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), ct);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: ct);

        // Drive the failing batch and wait for the turn to fail.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "run two tools"
        }, TimeSpan.FromSeconds(3), ct);

        await subscriber.FishForMessageAsync<object>(
            m => m is TurnCompleted { Outcome: TurnOutcome.Failed }, TimeSpan.FromSeconds(10),
            cancellationToken: ct);

        // Turn 2: a follow-up user message forces a fresh provider request whose
        // assembled messages ARE the conversation history (the error reply is
        // in-memory only and never persisted, so this is the only way to observe
        // the ordering the provider would see).
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "are you there?"
        }, TimeSpan.FromSeconds(3), ct);

        await subscriber.FishForMessageAsync<object>(
            m => m is TextOutput, TimeSpan.FromSeconds(10), cancellationToken: ct);

        // The last provider request is turn 2's; its messages are the assembled
        // history including turn 1's tool_calls message, tool results, and the
        // error reply.
        var assembled = _fakeChatClient.ReceivedMessages[^1];

        var toolCallIdx = -1;
        for (var k = 0; k < assembled.Count; k++)
        {
            if (assembled[k].Contents.OfType<FunctionCallContent>().Any())
            {
                toolCallIdx = k;
                break;
            }
        }

        Assert.True(toolCallIdx >= 0, "expected an assistant tool_calls message in the assembled history");

        var expectedIds = assembled[toolCallIdx].Contents.OfType<FunctionCallContent>()
            .Select(f => f.CallId)
            .ToHashSet(StringComparer.Ordinal);

        // Walk forward from the tool_calls message, collecting the contiguous run
        // of tool-result messages. A non-tool-result message (e.g. the error
        // reply) ends the run — exactly what the provider treats as the boundary.
        var answeredIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = toolCallIdx + 1; i < assembled.Count; i++)
        {
            var results = assembled[i].Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count == 0)
                break;
            foreach (var r in results)
                answeredIds.Add(r.CallId);
        }

        Assert.True(
            expectedIds.SetEquals(answeredIds),
            "An assistant tool_calls message must be immediately followed by a contiguous run of "
            + $"tool-result messages answering every call id. Expected [{string.Join(",", expectedIds)}] "
            + $"but the contiguous run covered [{string.Join(",", answeredIds)}]. "
            + $"Assembled roles: {string.Join(" -> ", assembled.Select(m => m.Role.Value))}");
    }
}

/// <summary>
/// Tool executor fake whose <see cref="InterpretToolCall"/> throws for chosen
/// call ids. A throw there escapes the pipeline's per-call try/catch (which
/// otherwise converts failures into tool-result error text), so it reaches
/// ToolExecutionFailed -> FailCurrentTurn — the partial-batch-failure path.
/// </summary>
internal sealed class PartialFailureToolExecutor : IToolExecutor
{
    public HashSet<string> FailInterpretForCallIds { get; } = new(StringComparer.Ordinal);

    public ToolCallInterpretation InterpretToolCall(FunctionCallContent toolCall)
    {
        if (FailInterpretForCallIds.Contains(toolCall.CallId))
            throw new InvalidOperationException(
                $"simulated interpret failure for {toolCall.CallId}");

        return new ToolCallInterpretation(null, null, toolCall);
    }

    public Task AuthorizeAsync(FunctionCallContent toolCall, Netclaw.Tools.ToolExecutionContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string> ExecuteAsync(FunctionCallContent toolCall, Netclaw.Tools.ToolExecutionContext context, CancellationToken ct = default)
        => Task.FromResult($"{toolCall.Name}-ok");
}
