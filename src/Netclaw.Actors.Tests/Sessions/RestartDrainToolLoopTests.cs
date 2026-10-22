// -----------------------------------------------------------------------
// <copyright file="RestartDrainToolLoopTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
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
    /// </summary>
    private sealed class ToolLoopChatClient : IChatClient
    {
        private int _mainCalls;

        public int MainCalls => Volatile.Read(ref _mainCalls);

        public TaskCompletionSource FollowUpStreaming { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FollowUpCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            if (call == 1)
            {
                yield return new ChatResponseUpdate(AiChatRole.Assistant,
                [
                    new FunctionCallContent(ToolCallId, "shell_execute",
                        new Dictionary<string, object?> { ["command"] = "python3 fetch_profile.py" })
                ])
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

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldNextCall()
            => Volatile.Write(ref _hold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Release() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

        public async Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext context,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            if (Volatile.Read(ref _hold) is { } hold)
                await hold.Task;

            return "Exit code: 0";
        }
    }
}
