// -----------------------------------------------------------------------
// <copyright file="ApprovalSecondAnswerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// A second answer to an approval prompt grants nothing and runs nothing. These specs pin what the operator
/// sees and what the sender receives: the prompt has an answer (reason <c>approval_already_answered</c>), not
/// that it expired. An answer for a call that never existed still gets the expired notice. The cases run
/// while the turn is live, after the turn ended, and after the session restarted.
/// </summary>
public sealed class ApprovalSecondAnswerTests : LlmSessionTestBase
{
    private const string AlreadyAnsweredText = "already has an answer";
    private const string ExpiredText = "approval prompt has expired";

    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly ApprovalGateToolExecutor _toolExecutor = new();

    public ApprovalSecondAnswerTests(ITestOutputHelper output) : base(output)
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
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_toolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        registry.Register(
            AIFunctionFactory.Create((string path) => $"read {path}", "read_file"),
            "read_file");
        services.AddSingleton(registry);
    }

    // One gated call. The first answer ends the turn. The second answer for the same prompt arrives when the
    // session holds no entry for that prompt in memory. The restart cases also drop the actor. It must still
    // say that the prompt has an answer, and it must not run or grant anything.
    [Theory]
    [InlineData(ApprovalOptionKeys.Deny, ApprovalOptionKeys.ApproveOnce, false)]
    [InlineData(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny, false)]
    [InlineData(ApprovalOptionKeys.Deny, ApprovalOptionKeys.ApproveOnce, true)]
    [InlineData(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny, true)]
    public async Task Second_answer_after_the_turn_ended_says_the_prompt_has_an_answer_and_runs_nothing(
        string firstKey, string secondKey, bool restartBeforeSecondAnswer)
    {
        var session = await StartGatedTurnAsync("call-after-turn");
        var firstReply = await session.AnswerAsync(session.Prompt.CallId, firstKey);
        Assert.IsType<CommandAck>(firstReply);
        await session.AwaitTurnCompletedAsync();
        var executionsAfterFirstAnswer = firstKey == ApprovalOptionKeys.ApproveOnce ? 1 : 0;
        Assert.Equal(executionsAfterFirstAnswer, _toolExecutor.SuccessfulExecutions);

        if (restartBeforeSecondAnswer)
            await session.RestartAsync();

        var secondReply = await session.AnswerAsync(session.Prompt.CallId, secondKey);
        var notices = await session.DrainTextAsync();

        var nack = Assert.IsType<CommandNack>(secondReply);
        Assert.Equal(ApprovalNackReasons.AlreadyAnswered, nack.Reason);
        Assert.Contains(notices, text => text.Contains(AlreadyAnsweredText, StringComparison.Ordinal));
        Assert.DoesNotContain(notices, text => text.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(executionsAfterFirstAnswer, _toolExecutor.SuccessfulExecutions);
    }

    // Reason-code proof for the live-turn case. Two prompts keep the turn live. The adopted spec in
    // ApprovalRehydrationTests checks the notice text. This spec checks the nack reason that channels read.
    [Fact]
    public async Task Second_answer_during_a_live_turn_replies_with_the_already_answered_reason()
    {
        var session = await StartGatedTurnAsync("call-live-shell", "call-live-read");
        var shell = session.Prompts["call-live-shell"].CallId;
        var read = session.Prompts["call-live-read"].CallId;

        Assert.IsType<CommandAck>(await session.AnswerAsync(shell, ApprovalOptionKeys.Deny));
        var secondReply = await session.AnswerAsync(shell, ApprovalOptionKeys.ApproveOnce);
        var notices = await session.DrainTextAsync();

        var nack = Assert.IsType<CommandNack>(secondReply);
        Assert.Equal(ApprovalNackReasons.AlreadyAnswered, nack.Reason);
        Assert.Contains(notices, text => text.Contains(AlreadyAnsweredText, StringComparison.Ordinal));
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        Assert.IsType<CommandAck>(await session.AnswerAsync(read, ApprovalOptionKeys.Deny));
        await session.AwaitTurnCompletedAsync();
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
    }

    // Control. A call ID that never existed has no answer to point at. The prompt is gone for the operator,
    // so the expired notice and reason stay. The cases cover a live wait, an ended turn, and a restart.
    [Theory]
    [InlineData(AnswerPhase.LiveWait)]
    [InlineData(AnswerPhase.AfterTurn)]
    [InlineData(AnswerPhase.AfterRestart)]
    public async Task Answer_for_a_call_that_never_existed_gets_the_expired_reason_and_notice(AnswerPhase phase)
    {
        var session = await StartGatedTurnAsync("call-known");
        if (phase != AnswerPhase.LiveWait)
        {
            Assert.IsType<CommandAck>(await session.AnswerAsync(session.Prompt.CallId, ApprovalOptionKeys.Deny));
            await session.AwaitTurnCompletedAsync();
        }

        if (phase == AnswerPhase.AfterRestart)
            await session.RestartAsync();

        var reply = await session.AnswerAsync(
            new Netclaw.Tools.ToolCallId("never-existed"), ApprovalOptionKeys.ApproveOnce);
        var notices = await session.DrainTextAsync();

        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Equal(ApprovalNackReasons.PromptExpired, nack.Reason);
        Assert.Contains(notices, text => text.Contains(ExpiredText, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(notices, text => text.Contains(AlreadyAnsweredText, StringComparison.Ordinal));
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);

        if (phase == AnswerPhase.LiveWait)
        {
            Assert.IsType<CommandAck>(await session.AnswerAsync(session.Prompt.CallId, ApprovalOptionKeys.Deny));
            await session.AwaitTurnCompletedAsync();
        }
    }

    public enum AnswerPhase
    {
        LiveWait,
        AfterTurn,
        AfterRestart
    }

    private int _sessionCounter;

    private async Task<GatedSession> StartGatedTurnAsync(params string[] callIds)
    {
        _toolExecutor.GatedTools.Add("shell_execute");
        _toolExecutor.GatedTools.Add("read_file");
        _fakeChatClient.ToolCallsOnFirstCall = callIds
            .Select((id, index) => index == 0
                ? new FunctionCallContent(id, "shell_execute",
                    new Dictionary<string, object?> { ["command"] = "git status" })
                : new FunctionCallContent(id, "read_file",
                    new Dictionary<string, object?> { ["path"] = "README.md" }))
            .ToList();

        var sessionId = new SessionId($"test-channel/second-answer-{++_sessionCounter}");
        var session = new GatedSession(this, sessionId);
        await session.JoinAsync();
        await session.SendRequestAsync();
        await session.CollectPromptsAsync(callIds.Length);
        return session;
    }

    private sealed class GatedSession
    {
        private readonly ApprovalSecondAnswerTests _owner;
        private readonly SessionId _sessionId;
        private readonly IActorRef _sessionManager;
        private Akka.TestKit.TestProbe _subscriber;

        public GatedSession(ApprovalSecondAnswerTests owner, SessionId sessionId)
        {
            _owner = owner;
            _sessionId = sessionId;
            _sessionManager = owner.ActorRegistry.Get<SessionManagerActorKey>();
            _subscriber = owner.CreateTestProbe($"second-answer-sub-{sessionId.Value.GetHashCode():x}");
        }

        public Dictionary<string, ToolInteractionRequest> Prompts { get; } = new(StringComparer.Ordinal);

        public ToolInteractionRequest Prompt => Prompts.Values.First();

        public async Task JoinAsync()
        {
            await _sessionManager.Ask<SessionJoined>(new JoinSession(_subscriber)
            {
                SessionId = _sessionId,
                Filter = OutputFilter.Full
            }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await _subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        }

        public async Task SendRequestAsync()
            => await _sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = _sessionId,
                Content = "Run git status",
                Source = _owner.RequesterSource("U-requester")
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        public async Task CollectPromptsAsync(int count)
        {
            while (Prompts.Count < count)
            {
                var prompt = await _subscriber.FishForMessageAsync<ToolInteractionRequest>(
                    item => !Prompts.ContainsKey(item.CallId.Value),
                    TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
                Prompts[prompt.CallId.Value] = prompt;
            }
        }

        public Task<ISessionResponse> AnswerAsync(Netclaw.Tools.ToolCallId callId, string optionKey)
            => _sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = callId,
                SelectedKey = new ApprovalOptionKey(optionKey),
                SenderId = new SenderId("U-requester")
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        public async Task AwaitTurnCompletedAsync()
            => await _subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // The session emits output to subscribers before it replies to the sender. A marker sent after the
        // reply therefore arrives behind every output that the answer caused.
        public async Task<List<string>> DrainTextAsync()
        {
            var texts = new List<string>();
            _subscriber.Ref.Tell(Akka.Done.Instance);
            await _subscriber.FishForMessageAsync<object>(message =>
            {
                if (message is TextOutput text)
                    texts.Add(text.Text);
                return message is Akka.Done;
            }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            return texts;
        }

        // Stops the session actor. The next join re-creates it from the journal and the snapshot. A new
        // subscriber joins because the old one watched the stopped actor.
        public async Task RestartAsync()
        {
            var escapedId = Uri.EscapeDataString(_sessionId.Value);
            var child = await _owner.Sys.ActorSelection($"/user/session-manager/{escapedId}")
                .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            _owner.Watch(child);
            _owner.Sys.Stop(child);
            await _owner.ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
            _subscriber = _owner.CreateTestProbe($"second-answer-recovered-sub-{_sessionId.Value.GetHashCode():x}");
            await JoinAsync();
        }
    }

    private MessageSource RequesterSource(string senderId) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(senderId),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(
            TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };
}
