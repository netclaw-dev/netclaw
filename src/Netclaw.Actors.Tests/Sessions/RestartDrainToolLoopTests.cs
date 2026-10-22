// -----------------------------------------------------------------------
// <copyright file="RestartDrainToolLoopTests.cs" company="Petabridge, LLC">
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
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using AkkaPersistence = Akka.Persistence.Persistence;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for issue #2601: a restart drain that arrives during a tool loop.
/// <para>
/// The first tool batch of a turn closes the inputs of that turn (<c>ToolBatchStarted</c>). After that batch, the
/// session has no selected input. On dev, <c>SessionExecutionLifetime.RequestRestart</c> starts the model grace timer
/// only when the turn has a selected input. A drain during a later model call therefore waits for the turn to end.
/// The tool loop does not stop at a batch boundary either. In production, the drain timed out after 20 seconds, and
/// the session got no restart reminder and gave no notice.
/// </para>
/// <para>
/// The spec uses virtual scheduler time. The drain gives a live model call a grace period of 2 seconds. The spec
/// moves the clock in steps of 3 seconds until the drain replies. Each watchdog timeout of the session is much longer
/// than the total time that the spec moves the clock.
/// </para>
/// </summary>
public sealed class RestartDrainToolLoopTests : LlmSessionTestBase
{
    private const string SystemPrompt = "You are a test assistant in a tool loop.";
    private const string ToolCallId = "call-tool-loop-1";
    private const string Requester = "U-tool-loop";
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly ToolLoopChatClient _chat = new();
    private readonly HoldingToolExecutor _tools = new();

    public RestartDrainToolLoopTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override bool UseTestScheduler => true;

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chat));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            // The spec controls passivation. An idle timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
                // A short history still reaches the summary call, so a spec can hold a compaction open. Compaction
                // starts only when a model reply reports a full context window.
                KeepRecentMessages = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(SystemPrompt));
        services.AddSingleton<IToolExecutor>(_tools);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    private IActorRef SessionManager => ActorRegistry.Get<SessionManagerActorKey>();

    // The live failure (22 Oct 2026): the tool batch is complete, and the next model call streams thinking text. The
    // drain must stop that model call after the grace period and acknowledge.
    [Fact]
    public async Task A_drain_during_the_model_call_after_a_tool_batch_stops_the_model_and_acknowledges()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/model-side");
        await JoinAsync(CreateTestProbe("model-side-subscriber"), sessionId);
        await SendAsync(sessionId, "Check the package profile");
        await _chat.FollowUpStreaming.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);

        // Act
        var prepared = await DrainAsync(sessionId);

        // Assert
        Assert.True(_chat.FollowUpCancelled.Task.IsCompleted,
            "The session acknowledged the drain, but the model call after the tool batch did not stop.");
        Assert.Equal(sessionId, prepared.SessionId);
        // The tool batch closed the user input of this turn. A replay of that input can repeat the tool effects, so
        // the drain creates no restart reminder for this turn.
        Assert.Null(prepared.RestartReminder);
        Assert.Equal(2, _chat.MainCalls);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
    }

    // The tool side of the window: the drain arrives while the tool runs, before the next model call starts. The
    // tool result completes the batch. The session must stop at that batch boundary and must not start a model call.
    [Fact]
    public async Task A_drain_during_a_tool_batch_stops_the_turn_at_the_batch_boundary()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/tool-side");
        await JoinAsync(CreateTestProbe("tool-side-subscriber"), sessionId);
        _tools.HoldNextCall();
        await SendAsync(sessionId, "Check the package profile");
        await _tools.Started.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var drain = SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        // A ping after the drain request proves that the session processed the request before the tool completes.
        await SessionManager.Ask<SessionJoined>(new JoinSession(CreateTestProbe("tool-side-ping"))
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, Deadline, TestContext.Current.CancellationToken);

        // Act
        _tools.Release();

        // Assert
        await AwaitAssertAsync(
            () =>
            {
                Assert.True(_chat.MainCalls == 1,
                    $"The session started model call {_chat.MainCalls} after the restart drain request.");
                AdvanceScheduler(TimeSpan.FromSeconds(3));
                Assert.True(drain.IsCompleted, "The restart drain did not finish after the tool batch completed.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        var prepared = await drain;
        Assert.Equal(sessionId, prepared.SessionId);
        Assert.Equal(1, _chat.MainCalls);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
    }

    // An input that the session accepted during the tool loop stays pending in the journal. The drain must record
    // reminder eligibility after it stops the model, so the restart reminder resumes that input.
    [Fact]
    public async Task A_drain_in_a_tool_loop_returns_a_restart_reminder_for_an_input_that_waits()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/waiting-input");
        await JoinAsync(CreateTestProbe("waiting-input-subscriber"), sessionId);
        await SendAsync(sessionId, "Check the package profile");
        await _chat.FollowUpStreaming.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await SendAsync(sessionId, "Also check the beta feed");
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);

        // Act
        var prepared = await DrainAsync(sessionId);

        // Assert
        var reminder = prepared.RestartReminder;
        if (reminder is null)
        {
            Assert.Fail("The drain returned no restart reminder, but an accepted input waits in the journal.");
            return;
        }

        Assert.Equal(DeliveryKind.CurrentSession, reminder.Delivery.Kind);
        Assert.Equal(sessionId.Value, reminder.Delivery.SessionId);
        Assert.Equal(ChannelType.Slack, reminder.Delivery.OriginChannelType);
        Assert.Equal(TrustAudience.Team, reminder.Audience);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
    }

    // The drain stops the turn, and no restart reminder can resume it. The session must tell the person in the
    // channel that the restart stopped the turn. Without the notice, the turn stops silently.
    [Fact]
    public async Task A_drain_that_stops_a_tool_loop_without_a_reminder_tells_the_user()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/user-notice");
        var subscriber = CreateTestProbe("user-notice-subscriber");
        await JoinAsync(subscriber, sessionId);
        await SendAsync(sessionId, "Check the package profile");
        await _chat.FollowUpStreaming.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        // Act
        var prepared = await DrainAsync(sessionId);

        // Assert
        Assert.Null(prepared.RestartReminder);
        var output = await subscriber.FishForMessageAsync(
            message => message is SessionDeactivated || IsRestartNotice(message),
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(IsRestartNotice(output),
            "The session stopped the turn for the restart and gave the user no notice before it stopped.");
    }

    // The buffered input runs in its own turn after the restart. The reminder does not resume the stopped tool loop,
    // so the user must also get the notice.
    [Fact]
    public async Task A_drain_in_a_tool_loop_tells_the_user_even_when_a_reminder_resumes_a_waiting_input()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/waiting-input-notice");
        var subscriber = CreateTestProbe("waiting-input-notice-subscriber");
        await JoinAsync(subscriber, sessionId);
        await SendAsync(sessionId, "Check the package profile");
        await _chat.FollowUpStreaming.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await SendAsync(sessionId, "Also check the beta feed");

        // Act
        var prepared = await DrainAsync(sessionId);

        // Assert
        Assert.NotNull(prepared.RestartReminder);
        var output = await subscriber.FishForMessageAsync(
            message => message is SessionDeactivated || IsRestartNotice(message),
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(IsRestartNotice(output),
            "The reminder resumes only the waiting input, not the stopped tool loop, but the session gave the user no notice.");
    }

    // Gap 1, no text: the model returns tool calls after the drain request and before the grace stop. The session
    // must not record or run that batch. The input of the turn stays pending, so the reminder resumes the turn.
    [Fact]
    public async Task Tool_calls_inside_the_grace_period_start_no_batch_and_keep_the_input_pending()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/grace-tool-calls");
        var subscriber = CreateTestProbe("grace-tool-calls-subscriber");
        await JoinAsync(subscriber, sessionId);
        var release = _chat.HoldFirstCall();
        await SendAsync(sessionId, "Check the package profile");
        await _chat.FirstCallHeld.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var drain = await RequestDrainAsync(sessionId);

        // Act: the model returns its tool calls inside the grace period.
        release.TrySetResult();
        await ToolCallsHandledAsync(drain);

        // Assert
        var prepared = await AwaitDrainAsync(drain, requireNoToolRun: true);
        var events = await ReadJournalAsync(sessionId);
        Assert.Contains(events, IsEvent("InputAdmitted"));
        Assert.DoesNotContain(events, IsEvent("ToolBatchStarted"));
        Assert.DoesNotContain(events, IsEvent("InputClosed"));
        Assert.Equal(0, _tools.Executions);
        Assert.True(prepared.RestartReminder is not null,
            "The input of the stopped turn stays pending, so the drain must return a restart reminder.");
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
    }

    // Gap 1, with text: the model streams text and then returns tool calls inside the grace period. The session
    // must not record or run that batch. The text reached the user, so the inputs close and the user gets the notice.
    [Fact]
    public async Task Tool_calls_after_streamed_text_inside_the_grace_period_close_the_input_and_tell_the_user()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/grace-tool-calls-text");
        var subscriber = CreateTestProbe("grace-tool-calls-text-subscriber");
        await JoinAsync(subscriber, sessionId);
        var release = _chat.HoldFirstCall(preamble: "Let me fetch the profile.");
        await SendAsync(sessionId, "Check the package profile");
        await subscriber.FishForMessageAsync<TextDeltaOutput>(
            _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var drain = await RequestDrainAsync(sessionId);

        // Act
        release.TrySetResult();
        await ToolCallsHandledAsync(drain);

        // Assert
        var prepared = await AwaitDrainAsync(drain, requireNoToolRun: true);
        var events = await ReadJournalAsync(sessionId);
        Assert.DoesNotContain(events, IsEvent("ToolBatchStarted"));
        Assert.Contains(events, IsEvent("InputClosed"));
        Assert.Equal(0, _tools.Executions);
        Assert.Null(prepared.RestartReminder);
        var output = await subscriber.FishForMessageAsync(
            message => message is SessionDeactivated || IsRestartNotice(message),
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(IsRestartNotice(output),
            "The streamed text closed the input, so no reminder resumes the turn, but the session gave the user no notice.");
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
    }

    // Gap 3: a tool batch reports a full context window, so the tool loop compacts. The drain arrives during the
    // compaction. At the compaction end the session stops the turn. The tool loop does not resume, so the user must
    // get the notice.
    [Fact]
    public async Task A_drain_during_a_tool_loop_compaction_tells_the_user()
    {
        // Arrange
        var sessionId = new SessionId("D-tool-loop/compaction");
        var subscriber = CreateTestProbe("compaction-subscriber");
        await JoinAsync(subscriber, sessionId);
        _chat.FirstCallUsage = new UsageDetails
        {
            InputTokenCount = 200_000,
            OutputTokenCount = 1,
            TotalTokenCount = 200_001
        };
        var summary = _chat.HoldSummary();
        await SendAsync(sessionId, "Check the package profile");
        await _chat.SummaryHeld.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var drain = await RequestDrainAsync(sessionId);

        // Act
        summary.TrySetResult();

        // Assert
        var prepared = await AwaitDrainAsync(drain, requireNoToolRun: false);
        Assert.Null(prepared.RestartReminder);
        Assert.Equal(1, _chat.MainCalls);
        var output = await subscriber.FishForMessageAsync(
            message => message is SessionDeactivated || IsRestartNotice(message),
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(IsRestartNotice(output),
            "The drain stopped the tool loop at the compaction end, and nothing resumes it, but the session gave the user no notice.");
    }

    // Sends the drain request. A join after the request proves that the session processed the request.
    private async Task<Task<DaemonRestartPrepared>> RequestDrainAsync(SessionId sessionId)
    {
        var drain = SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        await SessionManager.Ask<SessionJoined>(new JoinSession(CreateTestProbe("drain-ping"))
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, Deadline, TestContext.Current.CancellationToken);
        return drain;
    }

    // The spec must not move the clock before the session handles the tool calls. Otherwise the grace stop can win,
    // and the session never sees the tool calls. A session that drops the calls stops the turn at once, and a
    // session that keeps them starts the tool. Each outcome ends this wait.
    private async Task ToolCallsHandledAsync(Task<DaemonRestartPrepared> drain)
        => await Task.WhenAny(drain, _tools.Started.Task).WaitAsync(Deadline, TestContext.Current.CancellationToken);

    private async Task<DaemonRestartPrepared> AwaitDrainAsync(Task<DaemonRestartPrepared> drain, bool requireNoToolRun)
    {
        await AwaitAssertAsync(
            () =>
            {
                if (requireNoToolRun)
                {
                    Assert.True(_tools.Executions == 0,
                        "The session ran a tool batch that the model returned after the restart drain request.");
                }

                AdvanceScheduler(TimeSpan.FromSeconds(3));
                Assert.True(drain.IsCompleted, "The restart drain did not finish.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        return await drain;
    }

    private static Predicate<object> IsEvent(string typeName)
        => payload => string.Equals(payload.GetType().Name, typeName, StringComparison.Ordinal);

    // Reads the payloads of the session journal from the in-memory journal.
    private async Task<IReadOnlyList<object>> ReadJournalAsync(SessionId sessionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var persistenceId = $"session-{sessionId.Value}";
        var probe = CreateTestProbe("journal-reader-" + Guid.NewGuid().ToString("N"));
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var payloads = new List<object>();
        while (true)
        {
            switch (await probe.ExpectMsgAsync<object>(Deadline, cancellationToken: ct))
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

    private static bool IsRestartNotice(object message) => message switch
    {
        TextOutput text => text.Text.Contains("restart", StringComparison.OrdinalIgnoreCase),
        ErrorOutput error => error.Message.Contains("restart", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    // The drain gives a live model call 2 seconds. The timer runs on virtual time, so the spec moves the clock until
    // the drain replies. The first failed assertion names the missing step.
    private async Task<DaemonRestartPrepared> DrainAsync(SessionId sessionId)
    {
        var drain = SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            () =>
            {
                AdvanceScheduler(TimeSpan.FromSeconds(3));
                Assert.True(_chat.FollowUpCancelled.Task.IsCompleted,
                    "The restart drain did not cancel the model call that follows the tool batch.");
                Assert.True(drain.IsCompleted, "The restart drain did not finish.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        return await drain;
    }

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
            Source = RequesterSource()
        }, Deadline, TestContext.Current.CancellationToken);

    private static MessageSource RequesterSource() => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(Requester),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };

    /// <summary>
    /// Main model call 1 returns one tool call. Main model call 2 streams one thinking delta and then waits until its
    /// token is cancelled, as a slow model does. A later main model call returns a short text reply.
    /// A call without the session system prompt is a sidecar call, and it gets a short text reply.
    /// A spec can hold call 1 before its tool call (<see cref="HoldFirstCall"/>), give call 1 a usage report
    /// (<see cref="FirstCallUsage"/>), and hold the compaction summary call (<see cref="HoldSummary"/>).
    /// </summary>
    private sealed class ToolLoopChatClient : IChatClient
    {
        private int _mainCalls;

        public int MainCalls => Volatile.Read(ref _mainCalls);

        public TaskCompletionSource FollowUpStreaming { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FollowUpCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCallHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SummaryHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UsageDetails? FirstCallUsage { get; set; }

        private TaskCompletionSource? _firstCallRelease;
        private string? _firstCallPreamble;
        private TaskCompletionSource? _summaryRelease;

        // Main model call 1 streams the preamble, if any, and then waits for the returned gate before it returns the
        // tool call. A cancelled call ends without the tool call.
        public TaskCompletionSource HoldFirstCall(string? preamble = null)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstCallPreamble = preamble;
            Volatile.Write(ref _firstCallRelease, release);
            return release;
        }

        // The compaction summary call waits for the returned gate.
        public TaskCompletionSource HoldSummary()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _summaryRelease, release);
            return release;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => await GetStreamingResponseAsync(messages, options, cancellationToken)
                .ToChatResponseAsync(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => StreamAsync(messages.ToList(), cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var isMainCall = messages.Any(message =>
                message.Role == AiChatRole.System
                && message.Text?.Contains(SystemPrompt, StringComparison.Ordinal) == true);
            var call = isMainCall ? Interlocked.Increment(ref _mainCalls) : 0;
            var isSummaryCall = !isMainCall && messages.Any(message =>
                message.Role == AiChatRole.System
                && message.Text?.Contains("You are a session summarizer", StringComparison.Ordinal) == true);

            if (isSummaryCall && Interlocked.Exchange(ref _summaryRelease, null) is { } summaryRelease)
            {
                SummaryHeld.TrySetResult();
                await summaryRelease.Task.WaitAsync(cancellationToken);
            }

            if (call == 1)
            {
                // The session holds back the first text delta of a call, so the preamble streams as two deltas.
                if (_firstCallPreamble is { } preamble)
                {
                    var middle = preamble.Length / 2;
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, preamble[..middle]);
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, preamble[middle..]);
                }

                if (Interlocked.Exchange(ref _firstCallRelease, null) is { } firstCallRelease)
                {
                    FirstCallHeld.TrySetResult();
                    await firstCallRelease.Task.WaitAsync(cancellationToken);
                }

                List<AIContent> contents =
                [
                    new FunctionCallContent(ToolCallId, "shell_execute",
                        new Dictionary<string, object?> { ["command"] = "python3 fetch_profile.py" })
                ];
                if (FirstCallUsage is { } usage)
                    contents.Add(new UsageContent(usage));
                yield return new ChatResponseUpdate(AiChatRole.Assistant, contents)
                {
                    FinishReason = ChatFinishReason.ToolCalls
                };
                yield break;
            }

            if (call == 2)
            {
                yield return new ChatResponseUpdate(AiChatRole.Assistant,
                    [new TextReasoningContent("The card structure is")]);
                FollowUpStreaming.TrySetResult();
                // The stream ends only when the session cancels the call.
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() =>
                       {
                           FollowUpCancelled.TrySetResult();
                           stopped.TrySetCanceled(cancellationToken);
                       }))
                {
                    await stopped.Task;
                }

                yield break;
            }

            yield return new ChatResponseUpdate(AiChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Runs each tool call at once. After <see cref="HoldNextCall"/>, the next call waits until the spec calls
    /// <see cref="Release"/>.
    /// </summary>
    private sealed class HoldingToolExecutor : IToolExecutor
    {
        private TaskCompletionSource? _hold;

        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldNextCall()
            => Volatile.Write(ref _hold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Release() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

        public async Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext context,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            Started.TrySetResult();
            if (Volatile.Read(ref _hold) is { } hold)
                await hold.Task;

            return "Exit code: 0";
        }
    }
}
