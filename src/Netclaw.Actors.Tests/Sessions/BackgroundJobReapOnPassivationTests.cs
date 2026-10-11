// -----------------------------------------------------------------------
// <copyright file="BackgroundJobReapOnPassivationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration tests for reap-on-passivation: a session that submitted
/// background jobs must kill them (via the manager handshake) before its final
/// passivation snapshot, surface the reap to the agent exactly once on
/// rehydration, and never wedge on an unresponsive manager.
/// </summary>
public sealed class BackgroundJobReapOnPassivationTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _fakeChatClient = new();

    public BackgroundJobReapOnPassivationTests(ITestOutputHelper output) : base(output)
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
    public async Task Idle_passivation_defers_for_active_job_then_restart_reaps_and_recovers_once()
    {
        var jobManagerProbe = CreateTestProbe("job-manager");
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManagerProbe.Ref, overwrite: true);

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-bg-1", "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = "jekyll serve",
                    ["_background"] = true,
                    ["_rationale"] = "dev server"
                })
        ];

        var sessionId = new SessionId("test-channel/reap-on-passivation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("reap-sub");

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

        // The pipeline routes the background call to the (probe) manager.
        var startCmd = await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("jekyll serve", startCmd.Command);
        Assert.Equal(0, startCmd.TimeoutSeconds); // no kill timer without an explicit hint
        jobManagerProbe.Reply(new BackgroundJobStarted(
            new BackgroundJobId("reap-job-1"), "/tmp/jobs/reap-job-1/output.log"));

        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);

        // The active job blocks idle passivation, even with no subscriber veto.
        child.Tell(ReceiveTimeout.Instance);
        var activeJobJoin = await child.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, activeJobJoin.SessionId);
        await jobManagerProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);

        // Explicit restart still reaps an active job and waits for the ack.
        var restartTask = sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        var kill = await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, kill.SessionId);
        jobManagerProbe.Reply(new SessionJobsReaped(sessionId, 1));

        var restartAck = await restartTask;
        Assert.Equal(sessionId, restartAck.SessionId);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync(
            m => m is SessionDeactivated,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        // Rehydrate with a reaped record. That record must not block idle stop.
        var subscriberB = CreateTestProbe("reap-sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        var rehydratedChild = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(rehydratedChild);
        rehydratedChild.Tell(ReceiveTimeout.Instance);
        var repeatedKill = await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, repeatedKill.SessionId);
        jobManagerProbe.Reply(new SessionJobsReaped(sessionId, 0));
        await ExpectTerminatedAsync(
            rehydratedChild,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.FishForMessageAsync(
            m => m is SessionDeactivated,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        // The next turn must surface the reaped job exactly once.
        _fakeChatClient.ToolCallsOnFirstCall = null;
        var llmCallsBefore = _fakeChatClient.ReceivedMessages.Count;
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "How is the server doing?",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriberB.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var firstTurnPrompt = FlattenPromptsSince(llmCallsBefore);
        Assert.Contains("status: reaped", firstTurnPrompt);
        Assert.Contains("reap-job-1", firstTurnPrompt);
        Assert.Contains("/tmp/jobs/reap-job-1/output.log", firstTurnPrompt);

        // The reaped entry is pruned after that turn — the next turn's prompt
        // must not regenerate the block.
        var callsBeforeSecondTurn = _fakeChatClient.ReceivedMessages.Count;
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Anything else?",
            Source = RequesterSource("local-user")
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriberB.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // Turn 1's injected context block lives on in persisted history, so the
        // string still appears once in turn 2's prompt. Pruning means the block
        // is not REGENERATED — i.e. exactly one (historical) occurrence, not two.
        var secondTurnPrompt = FlattenPromptsSince(callsBeforeSecondTurn);
        Assert.Equal(1, CountOccurrences(secondTurnPrompt, "status: reaped"));
    }

    [Fact]
    public async Task Idle_passivation_defers_for_active_job_and_restart_proceeds_without_reap_ack()
    {
        var jobManagerProbe = CreateTestProbe("job-manager-silent");
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManagerProbe.Ref, overwrite: true);

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-bg-2", "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = "npm run dev",
                    ["_background"] = true,
                    ["_rationale"] = "dev server"
                })
        ];

        var sessionId = new SessionId("test-channel/reap-ack-timeout");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("timeout-sub");

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
            new BackgroundJobId("timeout-job-1"), "/tmp/jobs/timeout-job-1/output.log"));

        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);

        child.Tell(ReceiveTimeout.Instance);
        var activeJobJoin = await child.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, activeJobJoin.SessionId);
        await jobManagerProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);

        // Restart starts the reap request. It must finish after the bounded
        // acknowledgement timeout even when the manager stays silent.
        var restartTask = sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "config-reload"),
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        await ExpectTerminatedAsync(
            child,
            TimeSpan.FromSeconds(20),
            cancellationToken: TestContext.Current.CancellationToken);
        var restartAck = await restartTask;
        Assert.Equal(sessionId, restartAck.SessionId);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private string FlattenPromptsSince(int callIndex) =>
        string.Join("\n===\n", _fakeChatClient.ReceivedMessages
            .Skip(callIndex)
            .SelectMany(prompt => prompt)
            .Select(m => m.Text ?? string.Empty));

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
