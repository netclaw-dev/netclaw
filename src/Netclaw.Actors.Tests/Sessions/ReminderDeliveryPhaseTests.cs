// -----------------------------------------------------------------------
// <copyright file="ReminderDeliveryPhaseTests.cs" company="Petabridge, LLC">
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
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Review round 3, S1. The binding sends <c>DeliveryFailed</c> only after it handled <c>TurnCompleted</c>. That is
/// the moment a long reminder turn starts to compact. The session SHALL NOT drop the report in this phase.
/// Spec: <c>netclaw-session</c>, "A failed post on a reminder turn is visible to the operator".
/// </summary>
public sealed class ReminderCompactionDeliveryTests : ReminderDeliveryTestBase
{
    public ReminderCompactionDeliveryTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        // Last registration wins. A small window makes a usage of 800 tokens cross the 0.75 threshold (750).
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 1000
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                CompactionThreshold = 0.75,
                SnapshotInterval = 50,
                KeepRecentMessages = 0,
                TitleGenerationInterval = 0
            },
            IdleTimeout = TimeSpan.Zero
        });
    }

    [Fact]
    public async Task A_failed_post_that_the_binding_reports_while_the_session_compacts_raises_one_alert()
    {
        // Arrange: the reminder turn crosses the compaction threshold, and the summarizer call waits on a gate,
        // so the session stays in Compacting until the test releases it.
        ChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };
        var compactionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatClient.ObservationResponseGate = compactionGate;

        var sessionId = new SessionId("mattermost/chan1/reminder-thread-compacting");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        try
        {
            await AwaitAssertAsync(() =>
            {
                Assert.Contains(ChatClient.ReceivedMessages, call => call.Any(message =>
                    message.Role == Microsoft.Extensions.AI.ChatRole.System
                    && message.Text.Contains("You are a session summarizer", StringComparison.Ordinal)));
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

            // Act: the report reaches the session in Compacting. The buffered user message is the barrier. The session
            // acknowledges it from Compacting too, and it handles the earlier report first.
            await EventFilter.Error(contains: ReminderSlug).ExpectAsync(1, async () =>
            {
                SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
                await ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = "Buffered while compacting"
                }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);

            // Assert: the alert exists before compaction ends. A session that saved the report for later would fail here.
            Assert.Equal(1, AlertCount);
        }
        finally
        {
            compactionGate.TrySetResult();
        }

        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
    }
}

/// <summary>
/// Review round 3, S1. While a restart drain is pending, the session SHALL report a failed reminder post in
/// every phase that can receive <c>DeliveryFailed</c>. The drain ends the session soon, and the post has failed
/// already, so the Error log and the alert are the only trace. The session adds no nudge and makes no retry.
/// </summary>
public sealed class ReminderRestartDrainDeliveryTests : ReminderDeliveryTestBase
{
    public ReminderRestartDrainDeliveryTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task A_report_that_arrives_while_a_restart_drain_waits_for_the_model_raises_one_alert()
    {
        // Arrange: the reminder turn is recorded. A person's message holds the session in Processing, then the
        // daemon asks the session to drain for a restart. Both messages use one sender, so they keep their order.
        var sessionId = new SessionId("mattermost/chan1/reminder-thread-drain-processing");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey));
        var gate = await HoldSessionInProcessingAsync(sessionId);
        var session = await ResolveSessionAsync(sessionId);
        session.Tell(new PrepareForDaemonRestart(sessionId, "test-restart"));

        try
        {
            // Act
            await EventFilter.Error(contains: ReminderSlug).ExpectAsync(1, () =>
            {
                session.Tell(FailedPost(sessionId, completed.TurnNumber));
                return Task.CompletedTask;
            }, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, AlertCount);
        }
        finally
        {
            gate.TrySetResult();
        }

    }

    private static DeliveryFailed FailedPost(SessionId sessionId, TurnNumber turn) => new()
    {
        SessionId = sessionId,
        TurnNumber = turn,
        ChannelType = ChannelType.Mattermost,
        FailureKind = DeliveryFailureKind.TransportFailure,
        ErrorMessage = "Unauthorized"
    };
}

/// <summary>
/// Review round 3, S1. The <c>Passivating</c> handler of <c>DeliveryFailed</c> has a restart-drain branch. A session
/// reaches that state and stays there while it waits for the background job manager to confirm a reap. The session
/// SHALL report a failed reminder post that arrives in that state.
/// </summary>
public sealed class ReminderPassivatingDrainDeliveryTests : ReminderDeliveryTestBase
{
    public ReminderPassivatingDrainDeliveryTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        services.AddSingleton<IToolExecutor>(new PermissiveToolExecutor());

        var registry = new ToolRegistry();
        registry.Register(
            Microsoft.Extensions.AI.AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task A_report_that_arrives_while_a_restart_drain_waits_for_the_job_reap_raises_one_alert()
    {
        // Arrange: the reminder turn starts a background job, so the drain must reap it before the session stops.
        var jobManagerProbe = CreateTestProbe("job-manager");
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManagerProbe.Ref, overwrite: true);
        ChatClient.ToolCallsOnFirstCall =
        [
            new Microsoft.Extensions.AI.FunctionCallContent("call-bg-1", "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = "jekyll serve",
                    ["_background"] = true,
                    ["_rationale"] = "dev server"
                })
        ];

        var sessionId = new SessionId("mattermost/chan1/reminder-thread-drain-reap");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("drain-reap-subscriber");
        await JoinAsync(sessionManager, subscriber, sessionId);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Check the open pull requests",
            Source = ReminderSource(FireKey)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        jobManagerProbe.Reply(new BackgroundJobStarted(
            new BackgroundJobId("drain-reap-job"), "/tmp/jobs/drain-reap-job/output.log"));
        var completed = await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // The restart drain moves the idle session to Passivating. It waits for the reap reply, which the test holds back.
        var restart = sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "test-restart"),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await jobManagerProbe.ExpectMsgAsync<KillJobsForSession>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // Act
            await EventFilter.Error(contains: ReminderSlug).ExpectAsync(1, () =>
            {
                SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);
                return Task.CompletedTask;
            }, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, AlertCount);
        }
        finally
        {
            jobManagerProbe.Reply(new SessionJobsReaped(sessionId, 1));
            await restart;
        }
    }

    private sealed class PermissiveToolExecutor : IToolExecutor
    {
        public Task<ShellProcessLaunch> PrepareShellLaunchAsync(
            Microsoft.Extensions.AI.FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(new ShellProcessLaunch(
                ToolArgumentHelper.GetString(toolCall.Arguments, "Command")!,
                context.ResolveShellCwd(ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory"))
                    ?? throw new InvalidOperationException("The test launch requires a working directory."),
                context.Invocation,
                new Netclaw.Security.ShellCommandPolicy(TestShellEnvironment.Current),
                new Netclaw.Security.ToolPathPolicy(TestShellEnvironment.Current, []),
                static _ => Task.CompletedTask));

        public Task AuthorizeAsync(Microsoft.Extensions.AI.FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(Microsoft.Extensions.AI.FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.FromResult("ok");
    }
}
