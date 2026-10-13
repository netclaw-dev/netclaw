// -----------------------------------------------------------------------
// <copyright file="BackgroundJobReapSupersededTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Journal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Verifies reap epoch isolation across two idle stop attempts.
/// </summary>
public sealed class BackgroundJobReapSupersededTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _fakeChatClient = new();

    public BackgroundJobReapSupersededTests(ITestOutputHelper output) : base(output) { }

    protected override bool UseTestScheduler => true;

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
            // Passivation is driven explicitly via ReceiveTimeout.Instance.
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
        services.AddSingleton<IToolExecutor>(new PermissiveToolExecutor());

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Older_reap_reply_cannot_release_newer_passivation()
    {
        // Arrange: recover a reaped-only job record that permits idle passivation.
        var jobManagerProbe = CreateTestProbe("superseded-reap-manager");
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManagerProbe.Ref, overwrite: true);
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-superseded-reap", "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = "jekyll serve",
                    ["_background"] = true,
                    ["_rationale"] = "dev server"
                })
        ];

        var sessionId = new SessionId("test-channel/superseded-reap-passivation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("superseded-reap-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Start the dev server",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        jobManagerProbe.Reply(new BackgroundJobStarted(
            new BackgroundJobId("superseded-reap-job"), "/tmp/jobs/superseded-reap-job/output.log"));
        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var firstChild = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(firstChild);
        var restart = sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        jobManagerProbe.Reply(new SessionJobsReaped(sessionId, 1));
        await restart;
        await ExpectTerminatedAsync(firstChild, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, await CountReapEventsAsync(sessionId));

        _fakeChatClient.ToolCallsOnFirstCall = null;
        var resumed = CreateTestProbe("superseded-reap-resumed");
        await sessionManager.Ask<SessionJoined>(new JoinSession(resumed)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await resumed.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);

        // Act: start a reap, abort its passivation, then start another reap.
        child.Tell(ReceiveTimeout.Instance);
        var firstReap = await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, firstReap.SessionId);
        var oldReplyTo = jobManagerProbe.Sender;

        child.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = new TurnNumber(0),
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "stale-feedback"
        });
        Assert.Equal(sessionId, (await child.Ask<SessionJoined>(new JoinSession(resumed)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).SessionId);

        child.Tell(ReceiveTimeout.Instance);
        var secondReap = await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, secondReap.SessionId);
        var currentReplyTo = jobManagerProbe.Sender;
        Assert.NotEqual(oldReplyTo, currentReplyTo);

        // Act: deliver the old reply while the new attempt waits for its own commit.
        Watch(oldReplyTo);
        oldReplyTo.Tell(new SessionJobsReaped(sessionId, 0));
        await ExpectTerminatedAsync(oldReplyTo, cancellationToken: TestContext.Current.CancellationToken);
        await child.Ask<SessionJoined>(new JoinSession(resumed)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AdvanceScheduler(TimeSpan.FromMilliseconds(200));

        // Assert: the old reply cannot write a reap event or stop the new attempt.
        Assert.Equal(1, await CountReapEventsAsync(sessionId));
        await ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // Act: deliver the current reply and finish the new attempt.
        currentReplyTo.Tell(new SessionJobsReaped(sessionId, 0));
        await AwaitAssertAsync(async () =>
            Assert.Equal(2, await CountReapEventsAsync(sessionId)),
            duration: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await child.Ask<SessionJoined>(new JoinSession(resumed)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AdvanceScheduler(TimeSpan.FromMilliseconds(200));

        // Assert: the current reply permits one final output and termination.
        await ExpectTerminatedAsync(
            child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await resumed.FishForMessageAsync(
            m => m is SessionDeactivated,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, await CountReapEventsAsync(sessionId));
    }

    private async Task<int> CountReapEventsAsync(SessionId sessionId)
    {
        var replies = CreateTestProbe();
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, replies.Ref));

        var count = 0;
        while (true)
        {
            var reply = await replies.ExpectMsgAsync<object>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            switch (reply)
            {
                case MemoryJournal.ReplayedEvent replayed
                    when replayed.Persistent.Payload is SessionBackgroundJobsReaped evt
                         && evt.SessionId == sessionId:
                    count++;
                    break;
                case MemoryJournal.EventReplaySuccess:
                    return count;
                case MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The journal replay failed.", failure.Cause);
            }
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

    private sealed class PermissiveToolExecutor : IToolExecutor
    {
        public Task<ShellProcessLaunch> PrepareShellLaunchAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(new ShellProcessLaunch(
                ToolArgumentHelper.GetString(toolCall.Arguments, "Command")!,
                context.ResolveShellCwd(ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory"))
                    ?? throw new InvalidOperationException("The test launch requires a working directory."),
                context.Invocation,
                new Netclaw.Security.ShellCommandPolicy(TestShellEnvironment.Current),
                new Netclaw.Security.ToolPathPolicy(TestShellEnvironment.Current, []),
                static _ => Task.CompletedTask));

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.FromResult("ok");
    }
}
