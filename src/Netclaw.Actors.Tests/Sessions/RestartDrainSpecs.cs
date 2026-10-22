// -----------------------------------------------------------------------
// <copyright file="RestartDrainSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Journal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using AkkaPersistence = Akka.Persistence.Persistence;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// The restart drain contract (#2601). One harness drives a session to a drain point and sends the drain. One check
/// then proves the same postconditions for each drain point:
/// <list type="bullet">
/// <item>The drain acknowledges before the 20-second coordinator deadline (virtual time).</item>
/// <item>After the drain request, no model call starts and no tool batch starts or runs.</item>
/// <item>The input of the stopped turn stays pending only when the turn resumes. A reminder exists only when an input
/// stays pending.</item>
/// <item>When the turn does not resume, exactly one notice with the text for the drain reason posts. Otherwise no
/// notice posts.</item>
/// <item>When the session dropped tool calls, the next model request after the restart has a valid history.</item>
/// </list>
/// A drain point with a child run needs the child fixture. The notice rule for a child-only reminder is a pure decision,
/// so <see cref="RestartNoticeRuleTests"/> covers it.
/// </summary>
public sealed class RestartDrainSpecs : LlmSessionTestBase
{
    private const string SystemPrompt = "You are a test assistant in a tool loop.";
    private const string Requester = "U-drain";
    private const string TurnInputText = "Check the package profile";
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CoordinatorDeadline = TimeSpan.FromSeconds(20);

    private readonly ScriptedChatClient _chat = new();
    private readonly HoldingToolExecutor _tools = new();
    private readonly ControllableWorkingContextSnapshotProvider _snapshots = new();

    public RestartDrainSpecs(ITestOutputHelper output) : base(output)
    {
    }

    protected override bool UseTestScheduler => true;

    public enum DrainPoint
    {
        FirstModelCall,
        ModelCallAfterToolBatch,
        ToolBatchBoundary,
        GraceToolCalls,
        GraceToolCallsAfterText,
        GraceEmptyReplyRetry,
        ToolLoopCompaction,
        CompactionReplay,
        PreparedModelRequest,
        BufferedInput,
    }

    [Theory]
    [InlineData(DrainPoint.FirstModelCall, "config-reload", true, true)]
    [InlineData(DrainPoint.ModelCallAfterToolBatch, "daemon-stop", false, false)]
    [InlineData(DrainPoint.ToolBatchBoundary, "config-reload", false, false)]
    [InlineData(DrainPoint.GraceToolCalls, "config-reload", true, true)]
    [InlineData(DrainPoint.GraceToolCallsAfterText, "config-reload", false, false)]
    [InlineData(DrainPoint.GraceEmptyReplyRetry, "config-reload", true, true)]
    [InlineData(DrainPoint.ToolLoopCompaction, "config-reload", false, false)]
    [InlineData(DrainPoint.CompactionReplay, "config-reload", false, false)]
    [InlineData(DrainPoint.PreparedModelRequest, "config-reload", true, true)]
    [InlineData(DrainPoint.BufferedInput, "config-reload", false, true)]
    public async Task A_restart_drain_meets_the_drain_contract(
        DrainPoint point,
        string reason,
        bool turnResumes,
        bool reminderExpected)
    {
        // Arrange
        var sessionId = new SessionId($"D-drain/{point}-{reason}");
        var subscriber = CreateTestProbe("drain-subscriber");
        await JoinAsync(subscriber, sessionId);
        var release = await DriveToDrainPointAsync(point, sessionId, subscriber);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var before = new Activity(_chat.MainCalls, _tools.Runs, Count<ToolBatchStarted>(await ReadJournalAsync(sessionId)));

        // Act
        var drain = await RequestDrainAsync(sessionId, reason);
        if (release is not null)
        {
            // The clock must not move before the session handles the released work. Otherwise the model grace stop
            // can win, and the row does not test its drain point.
            var nextWork = Task.WhenAny(drain, _chat.NextCall, _tools.NextRun);
            release();
            await nextWork.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        }

        var prepared = await AwaitDrainAsync(drain, before);

        // Assert
        await AssertDrainContractAsync(sessionId, session, subscriber, prepared, before, reason, turnResumes, reminderExpected);
        if (point == DrainPoint.GraceToolCallsAfterText)
            await AssertValidHistoryAfterRestartAsync(sessionId);
    }

    // Drives the session to the drain point. Returns the action that releases held work after the drain request.
    private async Task<Action?> DriveToDrainPointAsync(DrainPoint point, SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        Action? release = null;
        Task reached = _chat.Reached.Task;
        switch (point)
        {
            case DrainPoint.FirstModelCall:
                _chat.Script(Step.StreamUntilCancelled);
                break;
            case DrainPoint.ModelCallAfterToolBatch or DrainPoint.BufferedInput:
                _chat.Script(Step.ToolCall, Step.StreamUntilCancelled);
                break;
            case DrainPoint.ToolBatchBoundary:
                _chat.Script(Step.ToolCall, Step.StreamUntilCancelled);
                release = _tools.HoldNextRun().SetResult;
                reached = _tools.Started.Task;
                break;
            case DrainPoint.GraceToolCalls:
                _chat.Script(Step.HeldToolCall);
                break;
            case DrainPoint.GraceToolCallsAfterText:
                _chat.Script(Step.HeldTextAndToolCall);
                break;
            case DrainPoint.GraceEmptyReplyRetry:
                _chat.Script(Step.HeldThinkingOnly);
                break;
            case DrainPoint.ToolLoopCompaction:
                _chat.Script(Step.ToolCallWithFullContext);
                break;
            case DrainPoint.CompactionReplay:
                // A completed turn gives the compaction enough history for a summary call, which the row holds.
                _chat.Script(Step.Text, Step.ToolCall, Step.ContextOverflow);
                await SendAsync(sessionId, "Earlier question");
                await subscriber.FishForMessageAsync<TurnCompleted>(
                    _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
                break;
            case DrainPoint.PreparedModelRequest:
                _chat.Script(Step.StreamUntilCancelled);
                var inspection = new TaskCompletionSource<WorkingContextSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _snapshots.Pending = inspection;
                reached = _snapshots.InvocationStarted.Task;
                release = () => inspection.SetResult(new WorkingContextSnapshot
                {
                    WorkingContext = WorkingContext.Empty,
                    Git = new GitWorkingContextInspection.Skipped()
                });
                break;
        }

        if (point is DrainPoint.GraceToolCalls or DrainPoint.GraceToolCallsAfterText or DrainPoint.GraceEmptyReplyRetry)
            release = _chat.ReleaseHeldCall;

        if (point is DrainPoint.ToolLoopCompaction or DrainPoint.CompactionReplay)
        {
            reached = _chat.SummaryHeld.Task;
            release = _chat.HoldSummary().SetResult;
        }

        await SendAsync(sessionId, TurnInputText);
        await reached.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        if (point == DrainPoint.BufferedInput)
            await SendAsync(sessionId, "Also check the beta feed");
        return release;
    }

    private sealed record Activity(int MainCalls, int ToolRuns, int ToolBatches);

    // Sends the drain request. A join after the request proves that the session processed the request.
    private async Task<Task<DaemonRestartPrepared>> RequestDrainAsync(SessionId sessionId, string reason)
    {
        var drain = SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, reason),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        await SessionManager.Ask<SessionJoined>(new JoinSession(CreateTestProbe("drain-ping"))
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, Deadline, TestContext.Current.CancellationToken);
        return drain;
    }

    // Moves the virtual clock in 3-second steps, but not past the coordinator deadline.
    private async Task<DaemonRestartPrepared> AwaitDrainAsync(Task<DaemonRestartPrepared> drain, Activity before)
    {
        var moved = TimeSpan.Zero;
        await AwaitAssertAsync(
            () =>
            {
                AssertNoWorkAfterDrain(before);
                if (!drain.IsCompleted && moved + TimeSpan.FromSeconds(3) < CoordinatorDeadline)
                {
                    AdvanceScheduler(TimeSpan.FromSeconds(3));
                    moved += TimeSpan.FromSeconds(3);
                }

                Assert.True(drain.IsCompleted,
                    $"The drain did not acknowledge before the coordinator deadline (virtual time moved {moved.TotalSeconds}s).");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        return await drain;
    }

    private void AssertNoWorkAfterDrain(Activity before)
    {
        Assert.True(_chat.MainCalls == before.MainCalls,
            $"The session started model call {_chat.MainCalls} after the drain request.");
        Assert.True(_tools.Runs == before.ToolRuns, "The session ran a tool after the drain request.");
    }

    // The shared postconditions of every drain point.
    private async Task AssertDrainContractAsync(
        SessionId sessionId,
        IActorRef session,
        Akka.TestKit.TestProbe subscriber,
        DaemonRestartPrepared prepared,
        Activity before,
        string reason,
        bool turnResumes,
        bool reminderExpected)
    {
        AssertNoWorkAfterDrain(before);
        var notices = new List<string>();
        await subscriber.FishForMessageAsync(
            message =>
            {
                if (message is TextOutput text)
                    notices.Add(text.Text);
                return message is SessionDeactivated;
            },
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);

        var events = await ReadJournalAsync(sessionId);
        Assert.True(Count<ToolBatchStarted>(events) == before.ToolBatches,
            "The session recorded a tool batch after the drain request.");
        var pending = PendingInputIds(events);
        var turnInput = events.OfType<InputAdmitted>().Single(input => input.UserMessage.Content == TurnInputText).InputId;
        Assert.True(pending.Contains(turnInput) == turnResumes, "The turn input must stay pending exactly when the turn resumes.");
        Assert.Equal(reminderExpected, prepared.RestartReminder is not null);
        Assert.True(reminderExpected == pending.Count > 0, "A restart reminder must exist exactly when an input stays pending.");

        if (turnResumes)
            Assert.Empty(notices);
        else
            Assert.Equal(RestartNoticeRuleTests.ExpectedNotice(reason), Assert.Single(notices));
    }

    // After the restart, the first model request must pair each tool call with its result.
    private async Task AssertValidHistoryAfterRestartAsync(SessionId sessionId)
    {
        var subscriber = CreateTestProbe("after-restart-subscriber");
        await JoinAsync(subscriber, sessionId);
        var calls = _chat.MainCalls;
        await SendAsync(sessionId, "Continue the work");
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
        var request = _chat.MainRequests[calls];
        var contents = request.SelectMany(message => message.Contents).ToArray();
        Assert.Equal(
            contents.OfType<FunctionCallContent>().Select(call => call.CallId).Order(),
            contents.OfType<FunctionResultContent>().Select(result => result.CallId).Order());
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chat));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            // The spec controls passivation. An idle timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
                // A short history still reaches the summary call, so a row can hold a compaction open.
                KeepRecentMessages = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(SystemPrompt));
        services.AddSingleton<IToolExecutor>(_tools);
        services.AddSingleton<IWorkingContextSnapshotProvider>(_snapshots);
        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    private IActorRef SessionManager => ActorRegistry.Get<SessionManagerActorKey>();

    private async Task JoinAsync(Akka.TestKit.TestProbe subscriber, SessionId sessionId)
    {
        await SessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, Deadline, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
        => await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(Deadline, TestContext.Current.CancellationToken);

    private async Task SendAsync(SessionId sessionId, string content)
        => await SessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = new MessageSource
            {
                ChannelType = ChannelType.Slack,
                SenderId = new SenderId(Requester),
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                Principal = PrincipalClassification.TrustedInternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
                ReceivedAt = FixedReceivedAt,
            }
        }, Deadline, TestContext.Current.CancellationToken);

    private static int Count<T>(IEnumerable<object> events) => events.OfType<T>().Count();

    private static IReadOnlyList<InputId> PendingInputIds(IReadOnlyList<object> events)
    {
        var closed = new HashSet<InputId>();
        foreach (var payload in events)
        {
            switch (payload)
            {
                case TurnRecorded turn:
                    closed.UnionWith(turn.ConsumedInputIds);
                    break;
                case ToolBatchStarted batch:
                    closed.UnionWith(batch.ConsumedInputIds);
                    break;
                case InputClosed input:
                    closed.UnionWith(input.InputIds);
                    break;
            }
        }

        return events.OfType<InputAdmitted>().Select(admitted => admitted.InputId).Where(id => !closed.Contains(id)).ToArray();
    }

    private async Task<IReadOnlyList<object>> ReadJournalAsync(SessionId sessionId)
    {
        var persistenceId = $"session-{sessionId.Value}";
        var probe = CreateTestProbe("journal-reader-" + Guid.NewGuid().ToString("N"));
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var payloads = new List<object>();
        while (true)
        {
            switch (await probe.ExpectMsgAsync<object>(Deadline, cancellationToken: TestContext.Current.CancellationToken))
            {
                case MemoryJournal.ReplayedEvent replayed when replayed.Persistent.PersistenceId == persistenceId:
                    payloads.Add(replayed.Persistent.Payload);
                    break;
                case MemoryJournal.EventReplaySuccess:
                    return payloads;
                case MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The journal replay failed.", failure.Cause);
            }
        }
    }

    private enum Step
    {
        ToolCall,
        ToolCallWithFullContext,
        StreamUntilCancelled,
        HeldToolCall,
        HeldTextAndToolCall,
        HeldThinkingOnly,
        ContextOverflow,
        Text,
    }

    /// <summary>
    /// Main model calls follow the script. A call after the script returns a short text reply. A call without the
    /// session system prompt is a sidecar call. The compaction summary call can wait for a gate.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _mainRequests = [];
        private Step[] _script = [];
        private int _mainCalls;
        private TaskCompletionSource _nextCall = NewGate();
        private TaskCompletionSource? _heldCallRelease;
        private TaskCompletionSource? _summaryRelease;

        public int MainCalls => Volatile.Read(ref _mainCalls);
        public Task NextCall => Volatile.Read(ref _nextCall).Task;
        // The session reached the drain point: a held call waits, or a call streams until it is cancelled.
        public TaskCompletionSource Reached { get; } = NewGate();
        public TaskCompletionSource SummaryHeld { get; } = NewGate();

        public IReadOnlyList<IReadOnlyList<ChatMessage>> MainRequests
        {
            get { lock (_mainRequests) { return _mainRequests.ToArray(); } }
        }

        public void Script(params Step[] steps) => _script = steps;

        public void ReleaseHeldCall() => Volatile.Read(ref _heldCallRelease)?.TrySetResult();

        public TaskCompletionSource HoldSummary()
        {
            var release = NewGate();
            Volatile.Write(ref _summaryRelease, release);
            return release;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => StreamAsync(messages.ToList(), cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!HasSystemText(messages, SystemPrompt))
            {
                if (HasSystemText(messages, "You are a session summarizer")
                    && Interlocked.Exchange(ref _summaryRelease, null) is { } summary)
                {
                    SummaryHeld.TrySetResult();
                    await summary.Task.WaitAsync(cancellationToken);
                }

                yield return new ChatResponseUpdate(AiChatRole.Assistant, "ok");
                yield break;
            }

            lock (_mainRequests)
                _mainRequests.Add(messages);
            var call = Interlocked.Increment(ref _mainCalls);
            Interlocked.Exchange(ref _nextCall, NewGate()).TrySetResult();
            var step = call <= _script.Length ? _script[call - 1] : (Step?)null;

            if (step is Step.HeldTextAndToolCall)
            {
                // The session holds back the first text delta of a call, so the text streams as two deltas.
                yield return new ChatResponseUpdate(AiChatRole.Assistant, "Let me fetch ");
                yield return new ChatResponseUpdate(AiChatRole.Assistant, "the profile.");
            }

            if (step is Step.HeldToolCall or Step.HeldTextAndToolCall or Step.HeldThinkingOnly)
            {
                var release = NewGate();
                Volatile.Write(ref _heldCallRelease, release);
                Reached.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            switch (step)
            {
                case Step.ToolCall or Step.HeldToolCall or Step.HeldTextAndToolCall:
                    yield return ToolCallUpdate(usage: null);
                    yield break;
                case Step.ToolCallWithFullContext:
                    yield return ToolCallUpdate(new UsageDetails
                    {
                        InputTokenCount = 200_000, OutputTokenCount = 1, TotalTokenCount = 200_001
                    });
                    yield break;
                case Step.HeldThinkingOnly:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, [new TextReasoningContent("Thinking only.")])
                    {
                        FinishReason = ChatFinishReason.Stop
                    };
                    yield break;
                case Step.ContextOverflow:
                    throw new InvalidOperationException("The request exceeds the maximum context length of the model.");
                case Step.StreamUntilCancelled:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, [new TextReasoningContent("The card is")]);
                    Reached.TrySetResult();
                    var stopped = NewGate();
                    using (cancellationToken.Register(() => stopped.TrySetCanceled(cancellationToken)))
                        await stopped.Task;
                    yield break;
                default:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
                    yield break;
            }
        }

        private static ChatResponseUpdate ToolCallUpdate(UsageDetails? usage)
        {
            List<AIContent> contents =
            [
                new FunctionCallContent("call-drain-1", "shell_execute",
                    new Dictionary<string, object?> { ["command"] = "python3 fetch_profile.py" })
            ];
            if (usage is not null)
                contents.Add(new UsageContent(usage));
            return new ChatResponseUpdate(AiChatRole.Assistant, contents) { FinishReason = ChatFinishReason.ToolCalls };
        }

        private static bool HasSystemText(IReadOnlyList<ChatMessage> messages, string text)
            => messages.Any(message => message.Role == AiChatRole.System
                                       && message.Text?.Contains(text, StringComparison.Ordinal) == true);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Runs each tool call at once. After <see cref="HoldNextRun"/>, the next run waits for the gate.</summary>
    private sealed class HoldingToolExecutor : IToolExecutor
    {
        private TaskCompletionSource? _hold;
        private TaskCompletionSource _nextRun = NewGate();
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);
        public Task NextRun => Volatile.Read(ref _nextRun).Task;
        public TaskCompletionSource Started { get; } = NewGate();

        public TaskCompletionSource HoldNextRun()
        {
            var hold = NewGate();
            Volatile.Write(ref _hold, hold);
            return hold;
        }

        public async Task<string> ExecuteAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            Interlocked.Exchange(ref _nextRun, NewGate()).TrySetResult();
            Started.TrySetResult();
            if (Interlocked.Exchange(ref _hold, null) is { } hold)
                await hold.Task;
            return "Exit code: 0";
        }
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The notice rule of a restart drain (#2601), as a pure decision. The stopped turn resumes only when the restart
/// reminder replays the inputs of that turn. A reminder that only reports a child run, or that only runs a buffered
/// input as a new turn, does not resume the stopped turn.
/// </summary>
public sealed class RestartNoticeRuleTests
{
    private const string Tail = "The turn did not finish, and it will not continue by itself. Send a message to continue.";

    internal static string ExpectedNotice(string reason) => reason switch
    {
        "daemon-stop" => $"Netclaw stopped this turn because the daemon is stopping. {Tail}",
        "config-reload" => $"Netclaw stopped this turn because the daemon is restarting. {Tail}",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    [Theory]
    // The reminder replays the pending input of the turn, so the turn resumes.
    [InlineData("Eligible", 1, "config-reload", false)]
    // A child-only or buffered-input reminder: no input of the turn stays pending.
    [InlineData("Eligible", 0, "config-reload", true)]
    // No reminder: a tool batch or streamed text closed the input.
    [InlineData("None", 0, "daemon-stop", true)]
    [InlineData("EmittedText", 1, "config-reload", true)]
    public void The_notice_posts_exactly_when_the_stopped_turn_does_not_resume(
        string eligibility,
        int pendingTurnInputCount,
        string reason,
        bool noticeExpected)
    {
        var notice = SessionExecutionLifetime.RestartStoppedTurnNotice(
            Enum.Parse<ReminderEligibility>(eligibility), pendingTurnInputCount, reason);

        Assert.Equal(noticeExpected ? ExpectedNotice(reason) : null, notice);
    }
}
