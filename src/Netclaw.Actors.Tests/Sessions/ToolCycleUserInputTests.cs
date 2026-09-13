// -----------------------------------------------------------------------
// <copyright file="ToolCycleUserInputTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolCycleUserInputTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private const int MaximumToolIterations = 10;
    private readonly PausedChatClient _client = new();
    private readonly PausedToolExecutor _executor = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 1000 });
        services.AddSingleton(new SessionConfig
        {
            MaxToolIterationsPerTurn = MaximumToolIterations,
            Tuning = new SessionTuning
            {
                CompactionThreshold = 0.75,
                KeepRecentMessages = 0,
                KeepRecentToolResults = 1,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(_executor);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "same result", "search_tools"), "builtin");
        services.AddSingleton(registry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_completion_resets_cycle_state_only_for_new_user_input(bool newUserInput)
    {
        var ct = TestContext.Current.CancellationToken;
        ConfigureRepeatedCalls();
        foreach (var decision in new[] { true, true, true, false })
            _client.Inner.PlannedToolCallDecisions.Enqueue(decision);
        _executor.PauseAtCall = 2;
        var sessionId = new SessionId("cycle-user-input/tool-completion");
        var subscriber = await StartRequestAsync(sessionId);

        await _executor.CallPaused.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (newUserInput)
            await SendNewRequestAsync(sessionId);
        _executor.ResumeCall.TrySetResult();
        await ExpectTurnCompletedAsync(subscriber);

        Assert.Equal(newUserInput ? 3 : 2, _executor.Count);
        Assert.Equal(4, _client.Inner.CallCount);
    }

    [Fact]
    public async Task New_user_input_discards_the_exhausted_budget_from_the_completed_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        static List<FunctionCallContent> DistinctCalls(int number)
        {
            var calls = Calls(number);
            calls[0].Arguments!["query"] = number;
            return calls;
        }

        _client.Inner.ToolCallsOnFirstCall = DistinctCalls(1);
        _client.Inner.AfterToolCallResponse = count =>
            _client.Inner.ToolCallsOnFirstCall = DistinctCalls(count + 1);
        _client.Inner.AlwaysReturnToolCalls = true;
        _executor.PauseAtCall = MaximumToolIterations;
        var sessionId = new SessionId("cycle-user-input/exhausted-budget");
        var subscriber = await StartRequestAsync(sessionId);

        await _executor.CallPaused.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(MaximumToolIterations, _executor.Count);
        await SendNewRequestAsync(sessionId);
        _client.Inner.PlannedToolCallDecisions.Enqueue(true);
        _client.Inner.PlannedToolCallDecisions.Enqueue(false);
        _executor.ResumeCall.TrySetResult();
        await ExpectTurnCompletedAsync(subscriber);

        Assert.Equal(MaximumToolIterations + 1, _executor.Count);
        Assert.Equal(MaximumToolIterations + 2, _client.Inner.CallCount);
        Assert.Contains("search_tools", _client.Inner.ReceivedToolNames[MaximumToolIterations]);
    }

    [Fact]
    public async Task User_input_queued_during_terminal_reply_restores_tools_for_the_new_request()
    {
        var ct = TestContext.Current.CancellationToken;
        ConfigureRepeatedCalls();
        _client.Inner.AlwaysReturnToolCalls = true;
        _client.Pause = PausePoint.ToolFreeReply;
        var sessionId = new SessionId("cycle-user-input/terminal-reply");
        var subscriber = await StartRequestAsync(sessionId);

        await _client.CallPaused.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(2, _executor.Count);
        await SendNewRequestAsync(sessionId);
        foreach (var decision in new[] { false, true, false })
            _client.Inner.PlannedToolCallDecisions.Enqueue(decision);
        _client.ResumeCall.TrySetResult();
        await ExpectTurnCompletedAsync(subscriber);
        await ExpectTurnCompletedAsync(subscriber);

        Assert.Equal(3, _executor.Count);
        Assert.Equal(7, _client.Inner.CallCount);
        Assert.Empty(_client.Inner.ReceivedToolNames[4]);
        Assert.Contains("search_tools", _client.Inner.ReceivedToolNames[5]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_compaction_resets_cycle_state_only_for_new_user_input(bool newUserInput)
    {
        var ct = TestContext.Current.CancellationToken;
        ConfigureRepeatedCalls();
        _client.Pause = PausePoint.Compaction;
        foreach (var decision in new[] { true, true, true, false })
            _client.Inner.PlannedToolCallDecisions.Enqueue(decision);
        foreach (var tokens in new[] { 100, 800, 100, 100 })
            _client.Inner.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = tokens });
        var sessionId = new SessionId("cycle-user-input/normal-compaction");
        var subscriber = await StartRequestAsync(sessionId);

        await _client.CallPaused.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(2, _executor.Count);
        if (newUserInput)
            await SendNewRequestAsync(sessionId);
        _client.ResumeCall.TrySetResult();
        await subscriber.FishForMessageAsync<object>(message => message is CompactionOutput,
            TimeSpan.FromSeconds(10), cancellationToken: ct);
        await ExpectTurnCompletedAsync(subscriber);

        Assert.Equal(newUserInput ? 3 : 2, _executor.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overflow_replay_preserves_text_only_state_unless_a_new_user_request_arrives(bool newUserInput)
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = new SessionId("cycle-user-input/overflow-replay");
        var subscriber = await StartRequestAsync(sessionId);
        await ExpectTurnCompletedAsync(subscriber);

        ConfigureRepeatedCalls();
        _client.Inner.AlwaysReturnToolCalls = true;
        _client.Pause = PausePoint.Compaction;
        var toolResponses = 0;
        _client.Inner.AfterToolCallResponse = count =>
        {
            _client.Inner.ToolCallsOnFirstCall = Calls(count + 1);
            if (++toolResponses == 4)
            {
                _client.Inner.PlannedExceptions.Enqueue(new ProviderException(
                    "maximum context length exceeded", "HTTP 400: maximum context length exceeded", statusCode: 400));
            }
        };
        await SendNewRequestAsync(sessionId);
        await _client.CallPaused.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(2, _executor.Count);
        if (newUserInput)
        {
            await SendNewRequestAsync(sessionId);
            _client.Inner.PlannedToolCallDecisions.Enqueue(true);
        }
        _client.Inner.PlannedToolCallDecisions.Enqueue(false);
        _client.ResumeCall.TrySetResult();
        await subscriber.FishForMessageAsync<object>(message => message is CompactionOutput,
            TimeSpan.FromSeconds(10), cancellationToken: ct);
        await ExpectTurnCompletedAsync(subscriber);

        Assert.Equal(newUserInput ? 3 : 2, _executor.Count);
        if (newUserInput)
            Assert.Contains("search_tools", _client.Inner.ReceivedToolNames[^1]);
        else
            Assert.Empty(_client.Inner.ReceivedToolNames[^1]);
    }

    private void ConfigureRepeatedCalls()
    {
        _client.Inner.ToolCallsOnFirstCall = Calls(1);
        _client.Inner.AfterToolCallResponse = count => _client.Inner.ToolCallsOnFirstCall = Calls(count + 1);
    }

    private static List<FunctionCallContent> Calls(int number) =>
    [
        new FunctionCallContent($"call-{number}", "search_tools", new Dictionary<string, object?>
        {
            ["_rationale"] = "Read the same value."
        })
    ];

    private async Task<TestProbe> StartRequestAsync(SessionId sessionId)
    {
        var subscriber = CreateTestProbe();
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId, Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        await SendNewRequestAsync(sessionId);
        return subscriber;
    }

    private Task<CommandAck> SendNewRequestAsync(SessionId sessionId)
        => ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId, Content = "Run the same tool once more. This is an explicit user request."
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static ValueTask<object> ExpectTurnCompletedAsync(TestProbe subscriber)
        => subscriber.FishForMessageAsync<object>(message => message is TurnCompleted,
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

    private enum PausePoint { None, ToolFreeReply, Compaction }

    private sealed class PausedChatClient : IChatClient
    {
        private int _didPause;
        public FakeChatClient Inner { get; } = new();
        public PausePoint Pause { get; set; }
        public TaskCompletionSource CallPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToList();
            var systemText = snapshot.FirstOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.System)?.Text;
            var compaction = systemText?.Contains("You are a session summarizer", StringComparison.Ordinal) == true;
            var shouldPause = Pause switch
            {
                PausePoint.Compaction => compaction,
                PausePoint.ToolFreeReply => !compaction && options?.Tools is not { Count: > 0 },
                _ => false
            };
            if (shouldPause && Interlocked.Exchange(ref _didPause, 1) == 0)
            {
                CallPaused.TrySetResult();
                await ResumeCall.Task.WaitAsync(cancellationToken);
            }
            return await Inner.GetResponseAsync(snapshot, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Inner.Dispose();
    }

    private sealed class PausedToolExecutor : IToolExecutor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public int PauseAtCall { get; set; }
        public TaskCompletionSource CallPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AuthorizeAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public async Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _count) == PauseAtCall)
            {
                CallPaused.TrySetResult();
                await ResumeCall.Task.WaitAsync(ct);
            }
            return "same result";
        }
    }
}
