// -----------------------------------------------------------------------
// <copyright file="SlashCommandSkillContentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// An inline slash command puts the skill body into the first model call of the
/// turn. The working-context snapshot is built asynchronously, so the call is
/// assembled after the command handler has returned.
/// </summary>
public sealed class SlashCommandSkillContentTests : LlmSessionTestBase
{
    private const string SkillBodyMarker = "SKILL-BODY-MARKER-7f3a";

    private readonly FakeChatClient _chatClient = new();
    private readonly GatedSnapshotProvider _snapshots = new();

    public SlashCommandSkillContentTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IWorkingContextSnapshotProvider>(_snapshots);
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton(new ToolRegistry());

        var skillDir = Path.Combine(TestPaths.BasePath, "slash-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, $"""
            ---
            name: demo-skill
            description: Demo skill.
            ---

            # Demo

            {SkillBodyMarker}
            """);
        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(new SkillEntry("demo-skill", "Demo", "Demo skill.", skillFile, skillDir, null));
        services.AddSingleton(skillRegistry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task First_model_call_after_an_inline_slash_command_carries_the_skill_body(bool delaySnapshot, bool failSnapshot)
    {
        _snapshots.Fail = failSnapshot;
        if (!delaySnapshot)
            _snapshots.Release();

        var sessionId = new SessionId($"test-channel/slash-body-{delaySnapshot}-{failSnapshot}");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("slash-body-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/demo-skill run it"
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await _snapshots.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (delaySnapshot)
            Assert.Equal(0, _chatClient.CallCount);
        _snapshots.Release();

        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var firstCall = _chatClient.ReceivedMessages[0];
        Assert.Contains(firstCall, message => message.Text.Contains(SkillBodyMarker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/demo-skill run it", TurnOutcome.Completed)]
    [InlineData("/unknown-command", TurnOutcome.Skipped)]
    public async Task Buffered_slash_command_acknowledges_only_its_original_sender_once(
        string command, TurnOutcome commandOutcome)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var deadline = TimeSpan.FromSeconds(10);
        var sessionId = new SessionId($"test-channel/slash-ack-{commandOutcome}");
        var subscriber = CreateTestProbe("slash-ack-subscriber");
        var originalSender = CreateTestProbe("slash-ack-original");
        var completionSender = CreateTestProbe("slash-ack-completion");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _chatClient.NextResponseGate = hold;
        _snapshots.Release();

        try
        {
            var session = await StartTurnWithBufferedCommandAsync(sessionId, command, subscriber, originalSender);

            // This fresh session has one model call. The probe replaces its result with the same call identity.
            session.Tell(new LlmResponseReceived
            {
                CallId = 1,
                Response = new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "First turn complete."))
            }, completionSender.Ref);

            var first = await subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, deadline, cancellationToken: cancellation);
            Assert.Equal(TurnOutcome.Completed, first.Outcome);
            var drained = await subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, deadline, cancellationToken: cancellation);
            Assert.Equal(commandOutcome, drained.Outcome);
            Assert.Equal(commandOutcome == TurnOutcome.Completed ? 2 : 1, _chatClient.CallCount);
            if (commandOutcome == TurnOutcome.Completed)
                Assert.Contains(_chatClient.ReceivedMessages[1],
                    message => message.Text.Contains(SkillBodyMarker, StringComparison.Ordinal));

            await AssertNoFurtherAcknowledgementAsync(session, sessionId, subscriber, originalSender);
            await AssertNoFurtherAcknowledgementAsync(session, sessionId, subscriber, completionSender);
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    private async Task<IActorRef> StartTurnWithBufferedCommandAsync(
        SessionId sessionId, string command, TestProbe subscriber, TestProbe originalSender)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var deadline = TimeSpan.FromSeconds(10);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hold the first turn."
        }, deadline, cancellation);
        await _chatClient.FirstCallEntered.Task.WaitAsync(deadline, cancellation);
        await AwaitAssertAsync(() => Assert.Null(_chatClient.NextResponseGate),
            deadline, cancellationToken: cancellation);
        Assert.Equal(1, _chatClient.CallCount);

        var session = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(deadline, cancellation);
        session.Tell(new SendUserMessage { SessionId = sessionId, Content = command }, originalSender.Ref);
        var ack = await originalSender.ExpectMsgAsync<CommandAck>(deadline, cancellationToken: cancellation);
        Assert.Equal(sessionId, ack.SessionId);
        Assert.Equal(1, _chatClient.CallCount);
        return session;
    }

    private static async Task AssertNoFurtherAcknowledgementAsync(
        IActorRef session, SessionId sessionId, TestProbe subscriber, TestProbe sender)
    {
        // The reply follows every prior persist callback. An extra ack reaches this probe before the reply.
        session.Tell(new JoinSession(subscriber) { SessionId = sessionId, Filter = OutputFilter.Full }, sender.Ref);
        var joined = await sender.ExpectMsgAsync<SessionJoined>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, joined.SessionId);
    }

    private sealed class GatedSnapshotProvider : IWorkingContextSnapshotProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; set; }

        public void Release() => _gate.TrySetResult();

        public async Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            if (Fail)
                throw new InvalidOperationException("snapshot failed");
            return new WorkingContextSnapshot
            {
                WorkingContext = context,
                Git = new GitWorkingContextInspection.Unavailable("test")
            };
        }
    }
}
