// -----------------------------------------------------------------------
// <copyright file="WaitingReminderIdleAbandonmentBackgroundJobTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
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
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: the idle abandonment of a half-answered batch must not reap a background job that the waiting
/// input starts.
/// <para>
/// A model reply holds two gated tool calls. The person answers one, and a reminder waits behind the other. The idle
/// timeout abandons the batch and runs the reminder. The reminder turn starts a background job. The normal idle path
/// never passivates a session that has a live job, because passivation kills the session jobs. The deferred
/// passivation after the abandonment must follow the same rule. After the job ends, the session passivates as usual.
/// </para>
/// </summary>
public sealed class WaitingReminderIdleAbandonmentBackgroundJobTests : ApprovalWaitTestBase
{
    private const string FirstCallId = "call-read-first";
    private const string SecondCallId = "call-write-second";
    private const string JobCallId = "call-bg-job";
    private const string JobId = "idle-job-1";
    private const string JobDeliveryKey = BackgroundJobManagerActor.JobDeliveryKeyPrefix + JobId;

    private readonly ConcurrentQueue<SessionId> _killedSessions = new();

    public WaitingReminderIdleAbandonmentBackgroundJobTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        // The last registration wins. The two gated tools differ from shell_execute, which starts the job.
        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string path) => $"read {path}", "read_file"),
            "read_file");
        registry.Register(
            AIFunctionFactory.Create((string path) => $"wrote {path}", "write_file"),
            "write_file");
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
        services.AddSingleton<IToolExecutor>(new BackgroundLaunchToolExecutor(ToolExecutor));
    }

    [Fact]
    public async Task The_session_keeps_a_job_that_the_waiting_reminder_starts_and_passivates_after_the_job_ends()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/idle-abandon-reminder-starts-job");
        var session = await ReachIdleAbandonmentAsync(sessionId, remindersTurnStartsJob: true);

        // Act: the reminder turn starts the job and ends.
        var subscriber = await JoinFullSubscriberAsync(sessionId);
        session.Tell(ReceiveTimeout.Instance);
        await ExpectTurnsAsync(subscriber, 1);

        // Assert: the live job keeps the session. Nobody kills the job.
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.Empty(_killedSessions);
        var join = await session.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, join.SessionId);

        // Act: the job ends and its result turn runs.
        await SendAsync(sessionId, "Job finished.", JobResultSource());
        await ExpectTurnsAsync(subscriber, 1);
        session.Tell(ReceiveTimeout.Instance);

        // Assert: with no live job, the normal idle rule applies.
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    // Control: the same flow without a job. The deferred passivation ends the session when the reminder turn ends.
    [Fact]
    public async Task The_session_passivates_when_the_waiting_reminder_starts_no_job()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/idle-abandon-reminder-starts-no-job");
        var session = await ReachIdleAbandonmentAsync(sessionId, remindersTurnStartsJob: false);

        // Act
        session.Tell(ReceiveTimeout.Instance);

        // Assert
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await AwaitReminderRunAsync();
        Assert.Empty(_killedSessions);
    }

    // A half-answered batch, a recovered session, and a reminder that waits. The caller sends the idle message.
    private async Task<IActorRef> ReachIdleAbandonmentAsync(SessionId sessionId, bool remindersTurnStartsJob)
    {
        var jobManager = CreateTestProbe("job-manager");
        jobManager.SetAutoPilot(new DelegateAutoPilot((sender, message) =>
        {
            switch (message)
            {
                case StartBackgroundJob:
                    sender.Tell(new BackgroundJobStarted(new BackgroundJobId(JobId), "/tmp/jobs/idle-job-1/output.log"));
                    break;
                case KillJobsForSession kill:
                    _killedSessions.Enqueue(kill.SessionId);
                    sender.Tell(new SessionJobsReaped(kill.SessionId, 1));
                    break;
            }

            return AutoPilot.KeepRunning;
        }));
        ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(jobManager.Ref, overwrite: true);

        await ParkTwoApprovalsAsync(sessionId);
        await StopSessionAsync(sessionId);
        await JoinNewSubscriberAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000200");

        var reply = await SessionManager.Ask<ISessionResponse>(
            AnswerFor(sessionId, FirstCallId),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        if (remindersTurnStartsJob)
        {
            FakeChat.PlannedResponses.Enqueue(
            [
                new FunctionCallContent(JobCallId, "shell_execute",
                    new Dictionary<string, object?>
                    {
                        ["command"] = "jekyll serve",
                        ["_background"] = true,
                        ["_rationale"] = "dev server"
                    })
            ]);
        }

        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        return session;
    }

    private async Task ParkTwoApprovalsAsync(SessionId sessionId)
    {
        ToolExecutor.GatedTools.Add("read_file");
        ToolExecutor.GatedTools.Add("write_file");
        FakeChat.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(FirstCallId, "read_file",
                new Dictionary<string, object?> { ["path"] = "README.md" }),
            new FunctionCallContent(SecondCallId, "write_file",
                new Dictionary<string, object?> { ["path"] = "notes.md" })
        ];

        var first = CreateTestProbe("first-subscriber");
        await JoinAsync(first, sessionId, OutputFilter.Full);
        await SendAsync(sessionId, "Read the file and write the notes", RequesterSource(Requester));
        for (var call = 0; call < 2; call++)
        {
            await first.ExpectMsgAsync<ToolCallOutput>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }

        for (var call = 0; call < 2; call++)
        {
            await first.ExpectMsgAsync<ToolInteractionRequest>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private async Task<TestProbe> JoinFullSubscriberAsync(SessionId sessionId)
    {
        var subscriber = CreateTestProbe("full-subscriber-" + Guid.NewGuid().ToString("N"));
        await JoinAsync(subscriber, sessionId, OutputFilter.Full);
        return subscriber;
    }

    private async Task AwaitReminderRunAsync()
        => await AwaitAssertAsync(
            () =>
            {
                Assert.Contains(FakeChat.ReceivedMessages, request => LastUserText(request) == ReminderText);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

    private static ToolInteractionResponse AnswerFor(SessionId sessionId, string callId)
        => new()
        {
            SessionId = sessionId,
            CallId = new ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(Requester)
        };

    // Mirrors the source that BackgroundJobManagerActor builds for a finished job.
    private static MessageSource JobResultSource() => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId("background-job-system"),
        MessageId = JobDeliveryKey,
        TurnId = new TurnId(JobDeliveryKey),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind(BackgroundJobManagerActor.SourceKind)
        },
        ReceivedAt = new DateTimeOffset(2026, 5, 17, 12, 5, 0, TimeSpan.Zero),
        BackgroundJobId = new BackgroundJobId(JobDeliveryKey)
    };

    // The approval gate fake cannot prepare a checked background launch. This wrapper adds that one operation.
    private sealed class BackgroundLaunchToolExecutor : IToolExecutor
    {
        private readonly ApprovalGateToolExecutor _inner;

        public BackgroundLaunchToolExecutor(ApprovalGateToolExecutor inner) => _inner = inner;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => _inner.ExecuteAsync(toolCall, context, ct);

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => _inner.AuthorizeAsync(toolCall, context, ct);

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
    }
}
