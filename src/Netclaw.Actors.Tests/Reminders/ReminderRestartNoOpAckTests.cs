// -----------------------------------------------------------------------
// <copyright file="ReminderRestartNoOpAckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Reminders;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Executable spec: the restart reminder on the Reminder-origin route (#2491) ends at the session acknowledgement
/// when the acknowledgement is a no-op (D2 decision 5). A no-op acknowledgement means that nothing is left to
/// resume, so no turn will run and no terminal output will come. The run must settle as success at once.
/// <para>
/// The seam is the real reminder execution actor and the real session actor, joined by the real session pipeline.
/// The actor system runs on <see cref="Akka.TestKit.TestScheduler"/>. The spec moves virtual time only to end the
/// 2-second grace period of the restart drain. After the run under test starts, virtual time never moves, so a run
/// that waits for the 20-minute stall timeout cannot settle, and the spec fails. It does not wait for real time.
/// </para>
/// </summary>
public sealed class ReminderRestartNoOpAckTests : ApprovalWaitTestBase
{
    public ReminderRestartNoOpAckTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override bool UseTestScheduler => true;

    [Fact]
    public async Task A_no_op_restart_ack_settles_the_reminder_run_as_success_at_once()
    {
        // Arrange: a Reminder-origin turn is interrupted by a restart. The first restart message resumes it and the
        // resumed turn ends. The reminder system then delivers the restart message a second time.
        var sessionId = new SessionId("reminder/restart-noop-ack");
        var (definition, resumeGate) = await InterruptAndRestartAsync(sessionId);
        Assert.IsType<CommandAck>(await SendRestartMessageAsync(sessionId, definition));
        resumeGate.TrySetResult();
        await AwaitAssertAsync(
            async () => Assert.Empty(PendingInputIds(await ReadJournalAsync(sessionId))),
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var probe = CreateTestProbe();

        // Act: the second delivery reaches a session that has nothing left to resume.
        StartExecution(probe, definition, sessionId);

        // Assert: the session acknowledges with a no-op, and the run settles as success. The scheduler never advanced,
        // so the run did not wait for the stall timeout.
        var settled = await probe.ReceiveOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(
            settled is ReminderExecutionCompleted,
            "A no-op restart acknowledgement must settle the run at once. The run is still open, and it waits for a "
            + "terminal turn that will never come. Virtual time did not move after the run started, so the run did not reach the stall timeout.");
        var completed = (ReminderExecutionCompleted)settled!;
        Assert.True(completed.Success, completed.ErrorMessage);
    }

    [Fact]
    public async Task A_restart_ack_that_resumes_a_turn_settles_the_run_when_the_resumed_turn_ends()
    {
        // Arrange: a Reminder-origin turn is interrupted by a restart. The restart message has work to resume.
        var sessionId = new SessionId("reminder/restart-resume-ack");
        var (definition, resumeGate) = await InterruptAndRestartAsync(sessionId);
        var probe = CreateTestProbe();
        var callsBefore = FakeChat.CallCount;

        // Act: the session acknowledges the message and starts the resumed turn. The model call waits.
        var parent = StartExecution(probe, definition, sessionId);
        await AwaitAssertAsync(
            () =>
            {
                Assert.True(FakeChat.CallCount > callsBefore, "The resumed model call did not start.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert: an acknowledgement that resumes work is not the end of the run. The resumed turn ends it.
        // Barrier: the session sends its ack to the execution actor before it starts the model call. An Identify that
        // the parent sends now queues behind that ack. A run that ended at the ack puts its completion on the probe
        // before the identity, because the parent forwards in order. No time passes.
        parent.Tell(Barrier.Instance);
        var first = await probe.ReceiveOneAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(first is ActorIdentity, $"An ack that resumes work must not end the run. First message: {first}");
        resumeGate.TrySetResult();
        var completed = await probe.ExpectMsgAsync<ReminderExecutionCompleted>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(completed.Success, completed.ErrorMessage);
    }

    // Runs a turn on the Reminder channel, stops the daemon with a graceful restart, and rejoins the session. The
    // model call of the resumed turn waits on the returned gate. The restart reminder is the one the session made.
    private async Task<(ReminderDefinition Definition, TaskCompletionSource ResumeGate)> InterruptAndRestartAsync(
        SessionId sessionId)
    {
        await JoinNewSubscriberAsync(sessionId);
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChat.NextResponseGate = firstGate;
        await SendAsync(sessionId, "Finish the interrupted task", ReminderChannelSource());
        await AwaitAssertAsync(
            () =>
            {
                Assert.True(FakeChat.CallCount > 0, "The held model call did not start.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // The session gives a running model call a 2-second grace period before the restart drain ends. That timer runs
        // on virtual time, so the spec moves the clock until the drain finishes. The run under test starts afterwards.
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var drain = SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await AwaitAssertAsync(
            () =>
            {
                AdvanceScheduler(TimeSpan.FromSeconds(3));
                Assert.True(drain.IsCompleted, "The restart drain did not finish.");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var prepared = await drain;
        var definition = Assert.IsType<ReminderDefinition>(prepared.RestartReminder);
        Assert.Equal(ChannelType.Reminder, definition.Delivery.OriginChannelType);
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        await JoinNewSubscriberAsync(sessionId);
        var resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChat.NextResponseGate = resumeGate;
        return (definition, resumeGate);
    }

    private async Task<ISessionResponse> SendRestartMessageAsync(SessionId sessionId, ReminderDefinition definition)
        => await SessionManager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = definition.Instructions,
            Source = RestartReminderSource(definition)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // Plays the reminder manager: it creates the execution actor and accepts its completion.
    private IActorRef StartExecution(Akka.TestKit.TestProbe probe, ReminderDefinition definition, SessionId sessionId)
    {
        Assert.Equal(sessionId.Value, definition.Delivery.SessionId);
        var pipeline = Host.Services.GetRequiredService<ISessionPipeline>();
        return Sys.ActorOf(Props.Create(() => new AcceptingParent(probe.Ref, definition, pipeline)));
    }

    private sealed class Barrier
    {
        public static readonly Barrier Instance = new();
    }

    private static MessageSource ReminderChannelSource() => new()
    {
        ChannelType = ChannelType.Reminder,
        SenderId = new SenderId("reminder-system"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero)
    };

    private sealed class AcceptingParent : ReceiveActor
    {
        public AcceptingParent(IActorRef probe, ReminderDefinition definition, ISessionPipeline pipeline)
        {
            var envelope = new ReminderEnvelope<ReminderPayload>(
                new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId),
                new ReminderKey(definition.Id.Value),
                definition.Schedule.FireAt!.Value,
                ReminderDeadline.Infinite,
                new ReminderPayload { Id = definition.Id });
            var child = Context.ActorOf(ReminderExecutionActor.CreateProps(
                Guid.NewGuid(), definition, pipeline, TimeProvider.System, envelope));
            Receive<Barrier>(_ => child.Tell(new Identify("barrier")));
            Receive<ActorIdentity>(identity => probe.Tell(identity));

            Receive<ReminderExecutionCompleted>(completed =>
            {
                probe.Tell(completed);
                Sender.Tell(new ReminderExecutionAccepted(completed.ExecutionId));
            });
        }
    }
}
