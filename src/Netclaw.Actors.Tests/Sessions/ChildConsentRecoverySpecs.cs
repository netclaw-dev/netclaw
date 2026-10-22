// -----------------------------------------------------------------------
// <copyright file="ChildConsentRecoverySpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Tasks 1.8 and 1.9. Approval notices reach the model at most once. Recovery closes every pending notice with no turn
/// and rebuilds the digest from the open requests. In PR 1 a restart withdraws every child request, so the rebuilt
/// open set is empty and recovery admits no notice. A pending notice is an ordinary automated input, so a binary
/// without this change reads it as automation by its principal alone.
/// Source: agent-event-channel "Delivery order and guarantees", "An older binary treats a notice as automation",
/// design D4 "Recovery (PR 1, Cut 4)".
/// </summary>
public sealed class ChildConsentRecoverySpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    /// <summary>
    /// Scenario "Recovery replaces a pending notice with the true state". A notice is admitted, and its turn waits in
    /// the model call when the session crashes. Recovery closes the notice with InputClosed and no turn. The restart
    /// withdrew the child request, so no request is open, and recovery admits no new notice.
    /// Planted bug: recovery resumes the notice like a child result notice, or admits a notice for an empty open set.
    /// </summary>
    [Fact]
    public async Task Recovery_closes_a_pending_notice_with_no_turn_and_admits_none_for_an_empty_open_set()
    {
        // Arrange: the notice turn waits in its model call.
        var sessionId = new SessionId("consent/recovery-closes");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-recover");
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request)
            .Any(text => text.StartsWith(OpenNoticeHeading, StringComparison.Ordinal)), hold);

        try
        {
            gate.TrySetResult();
            await FishChildPromptAsync(subscriber, asking.Run.RunId);
            await entered.WaitAsync(Deadline, Cancellation);
            var notice = Assert.Single(ConsentNotices(journal, sessionId));
            Assert.Equal(-1, ConsumedAt(journal, notice.InputId));

            // Act: a crash, and then a recovery.
            await StopAsync(await ResolveSessionAsync(sessionId));
            hold.TrySetResult();
            await JoinAsync(sessionId);
            await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, asking.Run.RunId),
                "The recovery must record the lost child."), Deadline, cancellationToken: Cancellation);
            await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);
            await BarrierAsync(sessionId, asking);

            // Assert
            var closedBy = journal.Committed.Where(evt => ConsumedBy(evt).Contains(notice.InputId)).ToArray();
            var closed = Assert.IsType<InputClosed>(Assert.Single(closedBy));
            Assert.Contains(notice.InputId, closed.InputIds);
            Assert.Single(ConsentNotices(journal, sessionId));
        }
        finally
        {
            hold.TrySetResult();
            gate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    /// <summary>
    /// The baseline rule: when recovery closes no pending notice, the rebuilt digest does not wake the parent. The
    /// model saw the requests in a consumed notice already.
    /// Planted bug: recovery admits a notice for every rebuilt digest.
    /// </summary>
    [Fact]
    public async Task Recovery_admits_no_notice_when_it_closed_none()
    {
        // Arrange: the notice ran, and the child still waits.
        var sessionId = new SessionId("consent/recovery-baseline");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-baseline");
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, asking.Run.RunId);
        await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        var notice = Assert.Single(ConsentNotices(journal, sessionId));

        // Act
        await RestartSessionAsync(sessionId);
        await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, asking.Run.RunId),
            "The recovery must record the lost child."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);
        await BarrierAsync(sessionId, asking);

        // Assert
        Assert.Single(ConsentNotices(journal, sessionId));
        Assert.DoesNotContain(journal.Committed.OfType<InputClosed>(), closed => closed.InputIds.Contains(notice.InputId));
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
    }

    /// <summary>
    /// Task 1.9. The notice that the session writes is an ordinary admitted input with no new field. After a rollback,
    /// an old binary reads it from the journal and classifies it as automated input by its principal alone, so it
    /// never abandons an open approval.
    /// Planted bug: the notice reuses field 14, a new field, or the person principal of the run owner.
    /// </summary>
    [Fact]
    public async Task A_pending_notice_reads_as_automation_under_the_principal_check_alone()
    {
        // Arrange
        var sessionId = new SessionId("consent/rollback");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-rollback");
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, asking.Run.RunId);
        await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        var written = ConsentNotices(journal, sessionId)[0];
        var serializer = new NetclawProtobufSerializer((Akka.Actor.ExtendedActorSystem)Sys);

        // Act
        var bytes = serializer.ToBinary(written);
        var row = Assert.IsType<InputAdmitted>(serializer.FromBinary(bytes, serializer.Manifest(written)));
        var proto = Netclaw.Actors.Serialization.Proto.InputAdmittedProto.Parser.ParseFrom(bytes);

        // Assert
        Assert.Null(row.ChildNotice);
        Assert.Null(proto.ChildNotice);
        Assert.False(proto.HasSourceChildRunId);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, row.TurnContext.RequesterPrincipal);
        Assert.True(SessionToolWorkflow.IsAutomation(row.TurnContext.RequesterPrincipal));
        Assert.Equal(written.UserMessage.Content, row.UserMessage.Content);
        await SettleNoticesAsync(journal, sessionId);
    }

    /// <summary>
    /// Round 3 review. The journal rejects the admission of an approval notice at the end of a turn that succeeded.
    /// The session stops, as after a persist failure, so recovery can close the notices and withdraw the children. It
    /// does not post a failure for the finished turn.
    /// Planted bug: the rejection fails "the running turn". The session posts "this turn failed" for a turn that it
    /// recorded as complete, and it keeps running with an open child request and no notice.
    /// </summary>
    [Fact]
    public async Task A_rejected_notice_at_turn_end_stops_the_session_with_no_failure_for_the_finished_turn()
    {
        // Arrange: a held child of Alice. Alice's model call waits at a gate, and the child asks during it.
        var sessionId = new SessionId("consent/rejected-notice-turn-end");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-reject-end");
        subscriber.Watch(await ResolveSessionAsync(sessionId));
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains("Finish the turn."), hold);

        try
        {
            await SendAsync(sessionId, "Finish the turn.", Operator(Alice));
            var aliceInput = await AwaitInputAdmittedAsync(journal, "Finish the turn.");
            await entered.WaitAsync(Deadline, Cancellation);
            gate.TrySetResult();
            await FishChildPromptAsync(subscriber, asking.Run.RunId);
            await BarrierAsync(sessionId, asking);
            Assert.Empty(ConsentNotices(journal, sessionId));

            // Act: the turn ends, and the journal rejects the notice that the turn end admits.
            journal.RejectNextConsentNotice();
            hold.TrySetResult();
            await journal.ConsentNoticeRejected.Task.WaitAsync(Deadline, Cancellation);

            // Assert: the session stops before it posts any failure.
            var outcome = await subscriber.FishForMessageAsync(message => message is ErrorOutput or Akka.Actor.Terminated
                    or TurnCompleted { Outcome: TurnOutcome.Failed }, Deadline, cancellationToken: Cancellation);
            Assert.True(outcome is Akka.Actor.Terminated,
                $"The session must stop with no failure for the finished turn, but it posted {Describe(outcome)}.");
            Assert.Contains(journal.Committed.OfType<TurnRecorded>(), turn => turn.ConsumedInputIds.Contains(aliceInput.InputId));
        }
        finally
        {
            hold.TrySetResult();
            gate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    /// <summary>
    /// Round 3 review. The journal rejects the admission of an approval notice while the session is idle and paused,
    /// and a reminder fire waits for the pause. The session stops. The next join recovers it: recovery withdraws the
    /// child requests, the pause ends, and the held fire runs.
    /// Planted bug: in Ready the rejection only writes a log line. The session keeps the pause with no notice, so the
    /// held fire never runs.
    /// </summary>
    [Fact]
    public async Task A_rejected_notice_in_an_idle_session_stops_it_and_recovery_runs_the_held_fire()
    {
        // Arrange: child A asks, and its notice runs. A reminder fire then waits for the pause.
        var sessionId = new SessionId("consent/rejected-notice-held-fire");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (first, firstGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-reject-a");
        var (second, secondGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-reject-b");
        var session = await ResolveSessionAsync(sessionId);
        var watcher = CreateTestProbe();
        watcher.Watch(session);
        const long fireMs = 1_761_055_200_000;

        try
        {
            firstGate.TrySetResult();
            await FishChildPromptAsync(subscriber, first.Run.RunId);
            await SettleAfterRequestAsync(journal, sessionId, first);
            AssertAck(await AskManagerAsync<ISessionResponse>(new SendUserMessage
            {
                SessionId = sessionId, Content = "Sweep the open pull requests.", Source = Fire("daily-pr-sweep", fireMs)
            }));
            await BarrierAsync(sessionId, first);
            var fire = Assert.Single(FireAdmissions(journal, sessionId, FireKey("daily-pr-sweep", fireMs)));
            Assert.Equal(-1, ConsumedAt(journal, fire.InputId));

            // Act: child B asks while the session is idle, and the journal rejects its notice.
            journal.RejectNextConsentNotice();
            secondGate.TrySetResult();
            await journal.ConsentNoticeRejected.Task.WaitAsync(Deadline, Cancellation);

            // Assert: the session stops. The next join recovers it, and the held fire runs.
            await watcher.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
            await JoinAsync(sessionId);
            await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<TurnRecorded>(),
                    turn => turn.ConsumedInputIds.Contains(fire.InputId)),
                Deadline, cancellationToken: Cancellation);
        }
        finally
        {
            firstGate.TrySetResult();
            secondGate.TrySetResult();
        }
    }

    private static string Describe(object message) => message switch
    {
        ErrorOutput error => $"an error: {error.Message}",
        TurnCompleted completed => $"a {completed.Outcome} turn completion",
        _ => message.GetType().Name
    };
}
