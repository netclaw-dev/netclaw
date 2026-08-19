// -----------------------------------------------------------------------
// <copyright file="LlmTurnResumeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers bounded turn-level resume after a mid-stream LLM call timeout (see
/// <c>LlmSessionActor.TryResumeAfterTimeout</c>). Evidence: correlated provider
/// stall storms (a few tokens then silence) previously burned the full watchdog
/// budget and then failed the turn terminally — in headless <c>chat -p</c> mode a
/// failed turn is a failed session with no external retry. These tests prove the
/// discard-and-resume mechanism, its retry budget, the structural (not
/// tool-iteration-gated) safety of resuming any call in the turn, and that a
/// resumed call's <see cref="TextDeltaOutput"/> stream never corrupts a
/// delta-accumulating consumer's final answer — using
/// <see cref="LlmSessionTestBase.UseTestScheduler"/> so the watchdog fires only on
/// an explicit <see cref="LlmSessionTestBase.AdvanceScheduler"/> — no wall-clock race.
/// </summary>
public sealed class LlmTurnResumeTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromSeconds(2);
    private readonly ResumeTestChatClient _chatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "turn-resume-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PrefillTimeout = FirstTokenTimeout,
            FirstTokenTimeout = FirstTokenTimeout,
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                TimeoutResumeRetryBudget = 2,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Timeout_with_no_tool_call_discards_partial_content_and_resumes_successfully()
    {
        const string partialMarker = "STALLED_PARTIAL_MARKER_SHOULD_NOT_APPEAR";
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("stalled chunk one ", partialMarker));
        // The resumed call also streams multiple real deltas (not a single-shot
        // completion) so the C1 proof below exercises the same delta-accumulation
        // path the dead call used, not just the TextOutput fallback.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.MultiDeltaTextThenComplete("Resumed answer ", "after timeout"));

        var sessionId = new SessionId("turn-resume/success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-success-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // First call: wait for genuine partial streaming (proves a real stall, not
        // an instant failure), then let the watchdog fire.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);

        // Collect EVERY output the turn emits, in order, through TurnCompleted — the
        // exact event stream a delta-accumulating subscriber (headless JSON
        // envelope, webhook/reminder ExecutionOutputAccumulator, chat TUI) sees.
        var events = new List<object>();
        var advanced = false;
        object msg;
        do
        {
            msg = await subscriber.ExpectMsgAsync<object>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
            events.Add(msg);

            if (!advanced && msg is TextDeltaOutput d && d.Delta.Contains(partialMarker, StringComparison.Ordinal))
            {
                advanced = true;
                AdvanceScheduler(FirstTokenTimeout);
                await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
            }
        } while (msg is not TurnCompleted);

        var completed = Assert.IsType<TurnCompleted>(msg);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // C1 proof: feed the exact production event sequence into the real
        // ExecutionOutputAccumulator (shared by ReminderExecutionActor and
        // WebhookExecutionActor). Before the TextStreamDiscarded fix this would
        // accumulate "stalled chunk one STALLED_PARTIAL_MARKER_SHOULD_NOT_APPEARResumed
        // answer after timeout" — the dead call's partial text glued to the
        // resumed call's answer.
        var accumulator = new ExecutionOutputAccumulator(new ToolName("notify_channel"));
        foreach (var evt in events.OfType<SessionOutput>())
            accumulator.ProcessOutput(evt);

        Assert.Equal("Resumed answer after timeout", accumulator.GetAccumulatedText());
        Assert.DoesNotContain(partialMarker, accumulator.GetAccumulatedText(), StringComparison.Ordinal);

        // The discard signal must land strictly between the dead call's last delta
        // and the resumed call's first delta — proving the actor clears
        // subscriber buffers before the resumed stream starts, not after.
        var discardIndex = events.FindIndex(e => e is TextStreamDiscarded);
        var deadMarkerIndex = events.FindIndex(e => e is TextDeltaOutput dd && dd.Delta.Contains(partialMarker, StringComparison.Ordinal));
        var resumedDeltaIndex = events.FindIndex(e => e is TextDeltaOutput rd && rd.Delta.Contains("Resumed answer", StringComparison.Ordinal));
        Assert.True(discardIndex > 0, "Expected a TextStreamDiscarded output for the resumed turn.");
        Assert.True(discardIndex > deadMarkerIndex, "Discard signal must arrive after the dead call's partial content.");
        Assert.True(resumedDeltaIndex > discardIndex, "Resumed call's deltas must arrive after the discard signal.");

        // The final TextOutput (independent of delta accumulation) must also be clean.
        var finalText = Assert.IsType<TextOutput>(events.OfType<TextOutput>().Single());
        Assert.Equal("Resumed answer after timeout", finalText.Text);
        Assert.DoesNotContain(partialMarker, finalText.Text, StringComparison.Ordinal);

        Assert.Equal(2, _chatClient.CallCount);

        // The resumed call re-issued the SAME messages as the dead call: identical
        // role/text sequence, proving no mutation and no extra user message.
        AssertIdenticalMessageLists(_chatClient.ReceivedMessages[0], _chatClient.ReceivedMessages[1]);

        // Persistence check: the discarded partial content must never have entered
        // _state.History — prove it by sending a follow-up turn and confirming the
        // marker never resurfaces in the conversation history sent to the LLM.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantText("third response"));
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second message"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextOutput t && t.Text == "third response",
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        var thirdCallMessages = _chatClient.ReceivedMessages[2];
        Assert.DoesNotContain(thirdCallMessages, m => m.Text?.Contains(partialMarker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Timeout_resume_budget_exhausted_fails_turn_exactly_as_before()
    {
        // Budget is 2 (configured in ConfigureSessionServices): the initial call
        // plus 2 resumes must all stall before the turn fails.
        for (var i = 0; i < 3; i++)
            _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas($"stall {i} chunk one ", $"stall {i} chunk two"));

        var sessionId = new SessionId("turn-resume/budget-exhausted");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-budget-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
            await subscriber.FishForMessageAsync<object>(
                m => m is TextDeltaOutput d && d.Delta.Contains($"stall {attempt} chunk two", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
            AdvanceScheduler(FirstTokenTimeout);
        }

        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var errorOutput = Assert.IsType<ErrorOutput>(error);
        Assert.Equal(ErrorCategory.Timeout, errorOutput.Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);

        // Exactly 3 calls: the original plus the 2-call resume budget. No fourth
        // (unbounded) resume attempt.
        Assert.Equal(3, _chatClient.CallCount);
    }

    [Fact]
    public async Task Timeout_during_restart_drain_fails_turn_without_resuming()
    {
        // Stall with multiple real deltas so the watchdog fire is a genuine stall,
        // not an instant failure.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("chunk one ", "chunk two"));

        var sessionId = new SessionId("turn-resume/restart-drain");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-restart-drain-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        // Request a coordinated daemon restart drain WHILE the call is in flight.
        // TryResumeAfterTimeout must refuse once this lands, even though the retry
        // budget (2) is not exhausted — resuming would keep the turn alive and
        // block the drain.
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        var drainTask = sessionManager.Ask<CommandAck>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // PrepareForDaemonRestart's own ack is deferred until the drain completes
        // (which only happens once this turn resolves), so it cannot be awaited
        // here without deadlocking. Instead, round-trip a second ask through the
        // same (sessionManager -> child) path and wait for ITS reply: sessionManager
        // forwards synchronously, so this reply cannot land before the drain
        // request was already dequeued and processed by the child, guaranteeing
        // _restartDrainRequested is set before the scheduler is advanced below. A
        // same-filter rejoin is a no-op that only acks the caller — it does not
        // re-emit SessionJoined to the subscriber, so nothing else to await here.
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        AdvanceScheduler(FirstTokenTimeout);

        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, Assert.IsType<ErrorOutput>(error).Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);

        // No resume attempt happened — exactly the one dead call.
        Assert.Equal(1, _chatClient.CallCount);

        // The drain completes and the session actor passivates, proving the failed
        // turn (not a resume) let the coordinated restart proceed. No observer is
        // configured in this test fixture, so passivation skips straight to its
        // short PassivationFinalStopDelay (100ms) grace window. The timer is
        // registered asynchronously on the actor's own dispatcher (after this
        // test's earlier AdvanceScheduler call already returned), so poll — each
        // retry nudges the virtual clock a little further until the actor has
        // caught up and the grace window timer fires.
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromMilliseconds(50));
            Assert.True(drainTask.IsCompleted, "Expected the restart drain to complete once the failed turn released passivation.");
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, (await drainTask).SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timeout_after_tool_call_dispatched_resumes_successfully()
    {
        // First call dispatches a tool call and completes normally.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantToolCall(
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test query" })));
        // Second call — the post-tool follow-up — stalls with multiple real deltas,
        // then times out. This is the dominant real-world failure: C2 found the
        // pre-fix ToolIterationCount gate refused resume for every one of the
        // motivating stall reports because they all happened after at least one
        // completed tool iteration. Safety here is structural, not gate-based: tool
        // dispatch only happens in HandleLlmResponseReceived on a fully completed
        // response, and this call times out mid-stream, so it can never have
        // dispatched a tool call itself.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("post-tool chunk one ", "post-tool chunk two"));
        // Third call — the resume — completes normally.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.MultiDeltaTextThenComplete("Resumed ", "after tool call"));

        var sessionId = new SessionId("turn-resume/tool-gate");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-gate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Drain the tool call/result from the first (successful) call.
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Second call (post-tool) stalls; let the watchdog fire.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("post-tool chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // Third call (the resume) completes cleanly — no ErrorOutput/TurnCompleted
        // in between, proving the turn resumed instead of failing.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        var text = await subscriber.FishForMessageAsync<object>(
            m => m is TextOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var finalText = Assert.IsType<TextOutput>(text);
        Assert.Equal("Resumed after tool call", finalText.Text);
        Assert.DoesNotContain("post-tool chunk", finalText.Text, StringComparison.Ordinal);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);

        // Exactly 3 calls: the tool-call round, the stalled follow-up, and the
        // resumed follow-up. The tool executor ran exactly once — resume never
        // dispatches a tool call, so there is no double execution.
        Assert.Equal(3, _chatClient.CallCount);
        Assert.Equal(1, _fakeToolExecutor.CallCount);

        // The resumed call (index 2) re-issued the SAME messages as the dead call
        // (index 1) — including the tool-call/tool-result content from the earlier
        // completed iteration — and exposed the same tools.
        AssertIdenticalMessageLists(_chatClient.ReceivedMessages[1], _chatClient.ReceivedMessages[2]);
        AssertIdenticalTools(_chatClient.ReceivedOptions[1], _chatClient.ReceivedOptions[2]);
    }

    [Fact]
    public async Task Resumed_calls_discarded_estimate_uses_the_previous_completed_calls_real_input_count()
    {
        // D4: EstimateInputTokens re-stringified the whole message list on every
        // ContinueFireLlmCall — a quadratic hot-path cost paid by every tool
        // iteration to serve only the (rare) resume path. The fix reuses
        // _lastInputTokenCount, the provider's REAL input count from the most
        // recently completed call, as the honest proxy instead.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.InstantToolCall(
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test query" }),
            usage: new UsageDetails { InputTokenCount = 500, OutputTokenCount = 20 }));
        // Post-tool follow-up stalls and times out.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("post-tool chunk one ", "post-tool chunk two"));
        // The resume completes normally with its own (different, larger) usage —
        // proving the reported estimate is call 1's real count, not call 3's.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.MultiDeltaTextThenComplete(
            "Resumed ", "after tool call",
            usage: new UsageDetails { InputTokenCount = 520, OutputTokenCount = 10 }));

        var sessionId = new SessionId("turn-resume/discarded-estimate-real-proxy");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-discarded-estimate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Drain the tool call/result from the first (successful) call, checking
        // its usage carries the real 500-token count that the resume will proxy.
        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var firstUsage = await subscriber.FishForMessageAsync<object>(
            m => m is UsageOutput, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(500, Assert.IsType<UsageOutput>(firstUsage).InputTokens);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Second call (post-tool) stalls; let the watchdog fire.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("post-tool chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // Third call (the resume) completes cleanly.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        var usage = await subscriber.FishForMessageAsync<object>(
            m => m is UsageOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var finalUsage = Assert.IsType<UsageOutput>(usage);

        // The discarded call's estimate is the REAL input count from call 1 (the
        // most recently completed call before the resume) — not a fabricated
        // character-count guess of call 2's (larger, tool-result-laden) list, and
        // not call 3's own (different) real count either.
        Assert.Equal(500, finalUsage.DiscardedResumeEstimatedInputTokens);
        Assert.Equal(1, finalUsage.DiscardedResumeAttempts);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
    }

    [Fact]
    public async Task Resumed_calls_discarded_estimate_is_null_when_no_prior_real_usage_exists()
    {
        // The session's FIRST call ever dies — no call has completed yet, so
        // _lastInputTokenCount is still 0 (never set). D4: report that honestly
        // as "no estimate" rather than fabricating a character-count guess.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("chunk one ", "chunk two"));
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.MultiDeltaTextThenComplete(
            "Resumed ", "answer",
            usage: new UsageDetails { InputTokenCount = 300, OutputTokenCount = 5 }));

        var sessionId = new SessionId("turn-resume/discarded-estimate-unknown");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-discarded-estimate-null-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        var usage = await subscriber.FishForMessageAsync<object>(
            m => m is UsageOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        var finalUsage = Assert.IsType<UsageOutput>(usage);

        // No completed call ever reported real usage this session — the estimate
        // must be null (an honest "unknown"), not a fabricated number, while the
        // attempt count still reports 1.
        Assert.Null(finalUsage.DiscardedResumeEstimatedInputTokens);
        Assert.Equal(1, finalUsage.DiscardedResumeAttempts);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
    }

    /// <summary>
    /// Asserts two message lists are identical in role, text, and tool-call /
    /// tool-result content — used to prove a resumed call re-sends the exact same
    /// prompt as the call it replaced, including turns with tool activity.
    /// </summary>
    private static void AssertIdenticalMessageLists(
        IReadOnlyList<ChatMessage> expected, IReadOnlyList<ChatMessage> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Role, actual[i].Role);
            Assert.Equal(expected[i].Text, actual[i].Text);

            var expectedCalls = expected[i].Contents.OfType<FunctionCallContent>().ToList();
            var actualCalls = actual[i].Contents.OfType<FunctionCallContent>().ToList();
            Assert.Equal(expectedCalls.Count, actualCalls.Count);
            for (var c = 0; c < expectedCalls.Count; c++)
            {
                Assert.Equal(expectedCalls[c].CallId, actualCalls[c].CallId);
                Assert.Equal(expectedCalls[c].Name, actualCalls[c].Name);
            }

            var expectedResults = expected[i].Contents.OfType<FunctionResultContent>().ToList();
            var actualResults = actual[i].Contents.OfType<FunctionResultContent>().ToList();
            Assert.Equal(expectedResults.Count, actualResults.Count);
            for (var r = 0; r < expectedResults.Count; r++)
            {
                Assert.Equal(expectedResults[r].CallId, actualResults[r].CallId);
                Assert.Equal(expectedResults[r].Result?.ToString(), actualResults[r].Result?.ToString());
            }
        }
    }

    /// <summary>
    /// Asserts two <see cref="ChatOptions"/> exposed the same tool names —
    /// proving a resumed call offered the LLM the same tool surface as the call
    /// it replaced.
    /// </summary>
    private static void AssertIdenticalTools(ChatOptions? expected, ChatOptions? actual)
    {
        var expectedNames = (expected?.Tools ?? []).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actualNames = (actual?.Tools ?? []).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedNames, actualNames);
    }
}

/// <summary>
/// Separate fixture from <see cref="LlmTurnResumeTests"/> because it needs
/// <see cref="SessionConfig.PrefillTimeout"/> and <see cref="SessionConfig.FirstTokenTimeout"/>
/// to differ by an order of magnitude — <see cref="LlmTurnResumeTests"/> deliberately
/// sets them equal so its tests do not depend on the watchdog's arm timeout. Covers
/// H3/D3: a resumed call carries the dead call's own <c>_anyContentStreamed</c>
/// value forward — a call that already streamed substantive content stays on the
/// promoted (tighter) budget through every keepalive that follows; a call that
/// died during prefill with zero content stays on the full prefill budget instead
/// of being cut short.
/// </summary>
public sealed class LlmTurnResumeWatchdogArmingTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrefillTimeout = TimeSpan.FromMinutes(30);
    private readonly ResumeTestChatClient _chatClient = new();

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "turn-resume-arming-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PrefillTimeout = PrefillTimeout,
            FirstTokenTimeout = FirstTokenTimeout,
            // Larger than PrefillTimeout so the prefill-stage-death test below can
            // advance the scheduler all the way to PrefillTimeout without the
            // keepalive-immune no-progress deadline (default 1200s = 20 minutes)
            // firing first and masking which timer actually fired.
            NoProgressTimeout = TimeSpan.FromHours(1),
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                // A budget of 1 keeps this a clean two-call scenario: the dead call,
                // then the resume whose own expiry exhausts the budget.
                TimeoutResumeRetryBudget = 1,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton(new ToolRegistry());
    }

    [Fact]
    public async Task Resumed_call_after_midstream_stall_stays_on_promoted_budget_through_a_keepalive()
    {
        // First call: stall after two real deltas so its own watchdog promotes to
        // FirstTokenTimeout before firing — a genuine mid-stream stall, not an
        // instant failure. _anyContentStreamed is true when this call dies.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallAfterDeltas("chunk one ", "chunk two"));
        // Resumed call: one content-free keepalive, then silence.
        // StallImmediately() cannot catch the D3 regression — it never reaches
        // OnStreamProgress at all, so it only proves the INITIAL arm value (which
        // was already correct even with the bug). The bug only shows once the
        // resumed call's watchdog is re-armed by a NON-substantive update: a
        // substantive delta would "promote" correctly by accident.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.KeepaliveThenStall());

        var sessionId = new SessionId("turn-resume/watchdog-arming-keepalive");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-arming-keepalive-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(
            m => m is TextDeltaOutput d && d.Delta.Contains("chunk two", StringComparison.Ordinal),
            TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        AdvanceScheduler(FirstTokenTimeout);

        // Drain the discard signal the resume emits before re-firing — otherwise
        // it sits unread in the probe's mailbox and trips the ExpectNoMsgAsync
        // check below on an unrelated, already-delivered message.
        await subscriber.FishForMessageAsync<object>(
            m => m is TextStreamDiscarded, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);

        // The resumed call's keepalive carries no observable output (a
        // content-free update never emits a TextDeltaOutput), so there is no
        // SessionOutput to fish for as a synchronization signal here. Wait
        // briefly on real wall-clock time for the invoker's fire-and-forget Tell
        // to reach and be processed by the actor before probing the watchdog's
        // re-arm — this is not a virtual-clock condition, just letting an
        // in-process async handoff settle.
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // If the keepalive incorrectly reverted the arm to the 30-minute prefill
        // budget (the D3 bug), advancing only FirstTokenTimeout here would never
        // fire the watchdog, and the bounded WaitForStreamInvocationAsync (M4)
        // below would time out instead of observing the failure.
        AdvanceScheduler(FirstTokenTimeout);

        // Budget is 1: the resume's own watchdog expiry exhausts it, so the turn
        // fails — proving the resumed call's watchdog stayed armed at
        // FirstTokenTimeout through the keepalive, not the 30-minute PrefillTimeout.
        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, Assert.IsType<ErrorOutput>(error).Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);
        Assert.Equal(2, _chatClient.CallCount);
    }

    [Fact]
    public async Task Resumed_call_after_prefill_stage_death_keeps_the_full_prefill_budget()
    {
        // First call: dies during prefill without streaming a single update — not
        // even a keepalive. _anyContentStreamed is false (no evidence this
        // provider is even alive) when the watchdog fires.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallImmediately());
        // Resumed call: also dies during prefill with zero content.
        _chatClient.Behaviors.Enqueue(ResumeCallBehavior.StallImmediately());

        var sessionId = new SessionId("turn-resume/watchdog-arming-prefill-death");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("resume-arming-prefill-death-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // First call dies at the full prefill budget — it is a fresh call, not a
        // resume, so it is armed on PrefillTimeout regardless of D3.
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);
        AdvanceScheduler(PrefillTimeout);

        // Drain the discard signal the resume emits before re-firing — otherwise
        // it sits unread in the probe's mailbox and trips the ExpectNoMsgAsync
        // check below on an unrelated, already-delivered message.
        await subscriber.FishForMessageAsync<object>(
            m => m is TextStreamDiscarded, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        await _chatClient.WaitForStreamInvocationAsync(TestContext.Current.CancellationToken);

        // Prove the resumed call is armed on the FULL prefill budget, not the
        // promoted budget: advancing only FirstTokenTimeout must NOT fail the
        // turn yet. If the fix incorrectly forced the promoted budget onto a
        // call with no streamed evidence (the "ALSO" half of D3), this would
        // already have failed by here.
        AdvanceScheduler(FirstTokenTimeout);
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // Advance the rest of the way to the full prefill budget — now it fires,
        // exhausting the retry budget (1) and failing the turn.
        AdvanceScheduler(PrefillTimeout - FirstTokenTimeout);

        var error = await subscriber.FishForMessageAsync<object>(
            m => m is ErrorOutput, TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCategory.Timeout, Assert.IsType<ErrorOutput>(error).Category);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);
        Assert.Equal(2, _chatClient.CallCount);
    }
}

/// <summary>
/// One configured behavior for a single <see cref="ResumeTestChatClient"/> call,
/// dequeued in call order.
/// </summary>
internal enum ResumeCallBehaviorKind { StallAfterDeltas, StallImmediately, InstantText, MultiDeltaTextThenComplete, InstantToolCall, KeepaliveThenStall }

internal sealed record ResumeCallBehavior(
    ResumeCallBehaviorKind Kind,
    string? Delta1 = null,
    string? Delta2 = null,
    string? Text = null,
    FunctionCallContent? ToolCall = null,
    UsageDetails? Usage = null)
{
    /// <summary>
    /// Streams two substantive text deltas (needed so the session's
    /// buffered-first-delta trick actually flushes visible content — a single
    /// delta stays buffered pending a second) then hangs forever, simulating a
    /// half-open provider stream: a few tokens, then silence.
    /// </summary>
    public static ResumeCallBehavior StallAfterDeltas(string delta1, string delta2)
        => new(ResumeCallBehaviorKind.StallAfterDeltas, Delta1: delta1, Delta2: delta2);

    /// <summary>
    /// Hangs forever without ever streaming a single update — not even a
    /// keepalive. Isolates the watchdog's initial arm timeout from its
    /// stream-progress promotion logic.
    /// </summary>
    public static ResumeCallBehavior StallImmediately()
        => new(ResumeCallBehaviorKind.StallImmediately);

    /// <summary>
    /// Streams one content-free keepalive update (empty <c>Contents</c>, no
    /// finish reason — mirrors a provider heartbeat like llama.cpp's
    /// <c>prompt_progress</c>) then hangs forever. Unlike
    /// <see cref="StallImmediately"/>, this reaches
    /// <c>ProcessingWatchdog.OnStreamProgress</c> once with a non-substantive
    /// update — the only update kind that can expose a re-arm that reverts a
    /// resumed call's promoted budget back to the full prefill budget (a
    /// substantive delta would promote correctly by accident, and
    /// <see cref="StallImmediately"/> never reaches the progress handler at all).
    /// </summary>
    public static ResumeCallBehavior KeepaliveThenStall()
        => new(ResumeCallBehaviorKind.KeepaliveThenStall);

    public static ResumeCallBehavior InstantText(string text)
        => new(ResumeCallBehaviorKind.InstantText, Text: text);

    /// <summary>
    /// Streams two substantive text deltas (same buffered-first-delta
    /// requirement as <see cref="StallAfterDeltas"/>) and then completes
    /// normally. The final text is <paramref name="delta1"/> + <paramref name="delta2"/>.
    /// An optional trailing <paramref name="usage"/> update lets a test prove
    /// what a subsequent resume's discarded-token estimate is computed from.
    /// </summary>
    public static ResumeCallBehavior MultiDeltaTextThenComplete(string delta1, string delta2, UsageDetails? usage = null)
        => new(ResumeCallBehaviorKind.MultiDeltaTextThenComplete, Delta1: delta1, Delta2: delta2, Usage: usage);

    /// <summary>
    /// Dispatches a tool call and completes normally. An optional trailing
    /// <paramref name="usage"/> update lets a test prove a LATER resume's
    /// discarded-token estimate is the real count from THIS completed call.
    /// </summary>
    public static ResumeCallBehavior InstantToolCall(FunctionCallContent toolCall, UsageDetails? usage = null)
        => new(ResumeCallBehaviorKind.InstantToolCall, ToolCall: toolCall, Usage: usage);
}

/// <summary>
/// Fake <see cref="IChatClient"/> with per-call scripted streaming behavior:
/// stall after a couple of substantive deltas (never completes), return text
/// instantly, or return a tool call instantly. Records every call's message list
/// and <see cref="ChatOptions"/> so tests can assert a resumed call re-sends the
/// identical prompt and tool surface.
/// </summary>
internal sealed class ResumeTestChatClient : IChatClient
{
    // Bounds every wait for the next streaming invocation so a regression in the
    // production resume/watchdog wiring fails the test with a clear
    // TimeoutException instead of hanging the test run indefinitely.
    private static readonly TimeSpan InvocationWaitTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private int _callCount;
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];
    private readonly List<ChatOptions?> _receivedOptions = [];
    private readonly Channel<int> _invocations =
        Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public int CallCount => _callCount;

    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages
    {
        get { lock (_gate) { return _receivedMessages.ToArray(); } }
    }

    public IReadOnlyList<ChatOptions?> ReceivedOptions
    {
        get { lock (_gate) { return _receivedOptions.ToArray(); } }
    }

    public Queue<ResumeCallBehavior> Behaviors { get; } = new();

    /// <summary>
    /// Awaits the next streaming invocation. The watchdog is already armed by
    /// then. Bounded by <see cref="InvocationWaitTimeout"/> — a regression that
    /// stops the actor from re-firing the call (e.g. resume silently not
    /// happening) fails with a <see cref="TimeoutException"/> instead of hanging.
    /// </summary>
    public async Task WaitForStreamInvocationAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(InvocationWaitTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _invocations.Reader.ReadAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {InvocationWaitTimeout} waiting for the next streaming invocation " +
                $"(callCount so far: {_callCount}).");
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        ResumeCallBehavior behavior;
        int callNumber;
        lock (_gate)
        {
            _receivedMessages.Add(messageList);
            _receivedOptions.Add(options);
            callNumber = ++_callCount;
            behavior = Behaviors.Count > 0
                ? Behaviors.Dequeue()
                : ResumeCallBehavior.InstantText($"[fake] default response #{callNumber}");
        }

        _invocations.Writer.TryWrite(callNumber);

        var updates = behavior.Kind switch
        {
            ResumeCallBehaviorKind.StallAfterDeltas => StallAfterDeltasAsync(behavior.Delta1!, behavior.Delta2!),
            ResumeCallBehaviorKind.StallImmediately => TestStreamingHelpers.NeverCompletesAsync(cancellationToken),
            ResumeCallBehaviorKind.KeepaliveThenStall => KeepaliveThenStallAsync(),
            ResumeCallBehaviorKind.InstantText => TestStreamingHelpers.ReturnTextAsync(behavior.Text!, cancellationToken),
            ResumeCallBehaviorKind.MultiDeltaTextThenComplete => MultiDeltaTextThenCompleteAsync(behavior.Delta1!, behavior.Delta2!),
            ResumeCallBehaviorKind.InstantToolCall => InstantToolCallAsync(behavior.ToolCall!, cancellationToken),
            _ => throw new InvalidOperationException($"Unhandled behavior kind {behavior.Kind}")
        };

        // A behavior that never completes (a stall) never reaches the trailing
        // usage update either — this only ever appends usage after a real
        // completion, matching what a real provider does.
        return AppendUsageIfPresent(updates, behavior.Usage);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> AppendUsageIfPresent(
        IAsyncEnumerable<ChatResponseUpdate> updates, UsageDetails? usage)
    {
        await foreach (var update in updates)
            yield return update;

        if (usage is not null)
            yield return new ChatResponseUpdate { Contents = [new UsageContent(usage)] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StallAfterDeltasAsync(string delta1, string delta2)
    {
        yield return new ChatResponseUpdate
        {
            Role = AiChatRole.Assistant,
            Contents = [new TextContent(delta1)]
        };
        await Task.Yield();

        yield return new ChatResponseUpdate
        {
            Contents = [new TextContent(delta2)]
        };
        await Task.Yield();

        // Stream is now silent — never completes on its own; the actor's watchdog
        // is the only thing that ends this turn.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.Task;
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> KeepaliveThenStallAsync()
    {
        // Content-free keepalive — empty Contents, no finish reason. Mirrors a
        // provider heartbeat that proves the socket is alive but carries no
        // model output (see StreamingResponseReader.IsSubstantiveUpdate).
        yield return new ChatResponseUpdate
        {
            Role = AiChatRole.Assistant,
            Contents = []
        };
        await Task.Yield();

        // Stream is now silent — never completes on its own; the actor's watchdog
        // is the only thing that ends this turn.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.Task;
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> MultiDeltaTextThenCompleteAsync(string delta1, string delta2)
    {
        yield return new ChatResponseUpdate
        {
            Role = AiChatRole.Assistant,
            Contents = [new TextContent(delta1)]
        };
        await Task.Yield();

        yield return new ChatResponseUpdate
        {
            Contents = [new TextContent(delta2)]
        };
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> InstantToolCallAsync(
        FunctionCallContent toolCall,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var response = new ChatResponse(new ChatMessage(AiChatRole.Assistant, [toolCall]));
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
