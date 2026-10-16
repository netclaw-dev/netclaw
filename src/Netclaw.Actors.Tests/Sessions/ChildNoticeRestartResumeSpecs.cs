// -----------------------------------------------------------------------
// <copyright file="ChildNoticeRestartResumeSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for the restart resume of pending inputs of two requesters. The daemon restart drain waits for a
/// grace period before it stops a model call. This class runs on the virtual scheduler, so a spec advances that period.
/// </summary>
public sealed class ChildNoticeRestartResumeSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    private const string AliceText = "Alice interrupted work.";
    private const string AliceLaterText = "Alice later work.";

    protected override bool UseTestScheduler => true;

    /// <summary>
    /// Spec 13 (S2-F5). The drain stops a session with an interrupted input of Alice and a notice of Bob. The resume runs
    /// the input of Alice and keeps the notice of Bob waiting. The notice of Bob runs in its own turn after that.
    /// Planted bug: the resume checks that all pending inputs share one full authority, so the mixed set blocks the resume.
    /// </summary>
    [Fact]
    public async Task Restart_resume_runs_one_requester_and_keeps_the_rest_waiting()
    {
        // Arrange and act: Bob's notice waits behind the input of Alice, and the daemon restarts.
        var sessionId = new SessionId("notice/restart-partition");
        var journal = await JournalAsync();
        var (accepted, requestsBeforeResume) = await RestartWithPendingAsync(sessionId, journal, laterAliceText: null);

        // Assert: the first resumed request holds Alice's text and not Bob's notice. Bob's notice runs later.
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray(),
                request => request.Any(message => message.Text == AliceText)) >= 0,
            "The resume must run Alice's interrupted input."), Deadline, cancellationToken: Cancellation);
        var resumed = Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray();
        var aliceRequest = IndexOf(resumed, request => request.Any(message => message.Text == AliceText));
        Assert.False(CarriesChildResult(resumed[aliceRequest], accepted.Run.RunId),
            "Alice's resumed turn must not carry Bob's result.");
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray(),
                request => CarriesNoticeFor(request, accepted.Run.RunId)) > aliceRequest,
            "Bob's notice must run after Alice's resumed turn."), Deadline, cancellationToken: Cancellation);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Equal(Bob, notice.TurnContext.RequesterSenderId?.Value);
    }

    /// <summary>
    /// Review p3. The pending list is Alice's input, Bob's notice, and a later input of Alice. The resume runs the input
    /// of Alice alone. The notice of Bob runs next, and the later input of Alice runs last, in admission order.
    /// Planted bug: the resume skips Bob's notice and counts the later input of Alice, so the first resumed turn
    /// takes the input of Alice and the notice of Bob together.
    /// </summary>
    [Fact]
    public async Task Restart_resume_keeps_admission_order_when_the_first_requester_returns_after_another()
    {
        // Arrange and act
        var sessionId = new SessionId("notice/restart-interleaved");
        var journal = await JournalAsync();
        var (accepted, requestsBeforeResume) = await RestartWithPendingAsync(sessionId, journal, AliceLaterText);

        // Assert
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray(),
                request => request.Any(message => message.Text == AliceLaterText)) >= 0,
            "The later input of Alice must run."), Deadline, cancellationToken: Cancellation);
        var resumed = Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray();
        var aliceRequest = IndexOf(resumed, request => request.Any(message => message.Text == AliceText));
        var noticeRequest = IndexOf(resumed, request => CarriesNoticeFor(request, accepted.Run.RunId));
        var laterRequest = IndexOf(resumed, request => request.Any(message => message.Text == AliceLaterText));
        Assert.True(aliceRequest >= 0, "The resume must run Alice's interrupted input.");
        Assert.False(CarriesChildResult(resumed[aliceRequest], accepted.Run.RunId),
            "Alice's resumed turn must not carry Bob's result.");
        Assert.DoesNotContain(resumed[aliceRequest], message => message.Text == AliceLaterText);
        Assert.True(noticeRequest > aliceRequest, "Bob's notice must run after Alice's resumed turn.");
        Assert.True(laterRequest > noticeRequest, "Alice's later input was admitted after Bob's notice, so it must run after it.");
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
    }

    /// <summary>
    /// Review S2. Alice has a live child. Her next turn streams text and stays open. The daemon restarts. The drain
    /// closes the input of that turn, because the reply is half sent, and the session must not repeat it. The session
    /// still owes the result of the child, so the drain must return a restart reminder. The reminder wakes the session
    /// after the restart, and the recovery reports the lost child with one notice.
    /// Planted bug: the check for the reminder returns early when the turn emitted text, even when a child is unreported.
    /// </summary>
    [Fact]
    public async Task Restart_after_streamed_text_still_wakes_a_session_that_owes_a_child_result()
    {
        // Arrange
        var sessionId = new SessionId("notice/restart-emitted-text");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var stream = NewGate();
        Clients.Parent.StreamCompletionGate = stream;
        await SendAsync(sessionId, AliceText, Operator(Alice));
        await subscriber.FishForMessageAsync<TextDeltaOutput>(_ => true, Deadline, cancellationToken: Cancellation);
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // Act: the drain waits for the model grace period, which the spec advances.
        var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromSeconds(3));
            Assert.True(drain.IsCompleted, "The restart drain did not finish.");
        }, Deadline, cancellationToken: Cancellation);
        var reminder = (await drain).RestartReminder;

        // Assert: the reminder uses the recorded context of the child.
        if (reminder is null)
            Assert.Fail("The drain returned no restart reminder, but a live child needs one.");
        Assert.Equal(TrustAudience.Personal, reminder.Audience);
        Assert.Equal(sessionId.Value, reminder.Delivery.SessionId);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);

        // After the restart the reminder wakes the session, and one notice reports the lost child.
        Clients.Parent.StreamCompletionGate = null;
        stream.TrySetResult();
        AssertAck(await SendRestartReminderAsync(sessionId, reminder));
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)), Deadline, cancellationToken: Cancellation);
        var resultPath = ResultPath(sessionId, accepted);
        await AwaitAssertAsync(() => Assert.True(File.Exists(resultPath), $"The runtime must write {resultPath}."),
            Deadline, cancellationToken: Cancellation);
        Assert.Contains("actor_stopped", await File.ReadAllTextAsync(resultPath, Cancellation), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review S2, tool loop form. Alice has a live child. Her next turn writes preamble text and opens an approval
    /// prompt. The daemon restarts while the prompt is parked. The prompt is durable, but the session still owes the
    /// result of the child, so the drain must return a restart reminder.
    /// Planted bug: the drain records no reminder for a turn that emitted text, although a child is unreported.
    /// </summary>
    [Fact]
    public async Task Restart_with_a_parked_approval_after_preamble_text_still_wakes_a_session_that_owes_a_child_result()
    {
        // Arrange
        var sessionId = new SessionId("notice/restart-text-and-approval");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        await SendAsync(sessionId, "probetext:p1", Operator(Alice));
        await FishApprovalAsync(subscriber, "probe-p1");
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // Act
        var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromSeconds(3));
            Assert.True(drain.IsCompleted, "The restart drain did not finish.");
        }, Deadline, cancellationToken: Cancellation);
        var reminder = (await drain).RestartReminder;

        // Assert
        if (reminder is null)
            Assert.Fail("The drain returned no restart reminder, but a live child needs one.");
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        AssertAck(await SendRestartReminderAsync(sessionId, reminder));
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)), Deadline, cancellationToken: Cancellation);
    }

    // Bob's child ends while Alice's turn is in flight, so Bob's notice waits behind the input of Alice. With a later
    // text, Alice writes again after the notice. The drain stops the session, and the reminder wakes it. The result is
    // the child run of Bob and the number of model requests before the reminder.
    private async Task<(ChildRunAccepted Accepted, int RequestsBeforeResume)> RestartWithPendingAsync(
        SessionId sessionId, AcceptanceFault journal, string? laterAliceText)
    {
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, bobGate) = await StartChildAsync(sessionId, Bob, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var busy = NewGate();
        Clients.Parent.NextResponseGate = busy;
        await SendAsync(sessionId, AliceText, Operator(Alice));
        bobGate.TrySetResult();
        await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, accepted.Run.RunId),
            "Bob's result must be ready while Alice's turn runs."), Deadline, cancellationToken: Cancellation);
        if (laterAliceText is not null)
            await SendAsync(sessionId, laterAliceText, Operator(Alice));
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // The drain waits for the model grace period, which a spec advances. Then the daemon restarts.
        var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));
        await AwaitAssertAsync(() =>
        {
            AdvanceScheduler(TimeSpan.FromSeconds(3));
            Assert.True(drain.IsCompleted, "The restart drain did not finish.");
        }, Deadline, cancellationToken: Cancellation);
        var reminder = (await drain).RestartReminder;
        if (reminder is null)
            Assert.Fail("The drain returned no restart reminder for the interrupted input.");
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        var requestsBeforeResume = Clients.Parent.ReceivedMessages.Count;
        var resumeReply = await SendRestartReminderAsync(sessionId, reminder);
        if (resumeReply is CommandNack nack)
            Assert.Fail($"The resume was refused: {nack.Reason}");
        return (accepted, requestsBeforeResume);
    }
}
