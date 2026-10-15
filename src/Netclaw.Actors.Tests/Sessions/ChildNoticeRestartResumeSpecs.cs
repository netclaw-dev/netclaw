// -----------------------------------------------------------------------
// <copyright file="ChildNoticeRestartResumeSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for the restart resume of pending inputs of two requesters. The daemon restart drain waits for a
/// grace period before it stops a model call. This class runs on the virtual scheduler, so a spec advances that period.
/// </summary>
public sealed class ChildNoticeRestartResumeSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    protected override bool UseTestScheduler => true;

    /// <summary>
    /// Spec 13 (S2-F5). The drain stops a session with an interrupted input of Alice and a notice of Bob. The resume runs
    /// the input of Alice and keeps the notice of Bob waiting. The notice of Bob runs in its own turn after that.
    /// Planted bug: the resume checks that all pending inputs share one full authority, so the mixed set blocks the resume.
    /// </summary>
    [Fact]
    public async Task Restart_resume_runs_one_requester_and_keeps_the_rest_waiting()
    {
        // Arrange: Bob's child ends while Alice's turn is in flight, so the notice of Bob waits behind the input of Alice.
        const string aliceText = "Alice interrupted work.";
        var sessionId = new SessionId("notice/restart-partition");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, bobGate) = await StartChildAsync(sessionId, Bob, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var busy = NewGate();
        Clients.Parent.NextResponseGate = busy;
        await SendAsync(sessionId, aliceText, Operator(Alice));
        bobGate.TrySetResult();
        await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, accepted.Run.RunId),
            "Bob's result must be ready while Alice's turn runs."), Deadline, cancellationToken: Cancellation);
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // Act: the drain waits for the model grace period, which a spec advances. Then the daemon restarts.
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
        var fire = reminder.Schedule.FireAt ?? throw new InvalidOperationException("The restart reminder has no fire time.");
        var resumeReply = await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = reminder.Instructions,
            Source = Reminder($"{reminder.Id.Value}:{fire.ToUnixTimeMilliseconds()}")
        });
        if (resumeReply is CommandNack nack)
            Assert.Fail($"The resume was refused: {nack.Reason}");

        // Assert: the first resumed request holds Alice's text and not Bob's notice. Bob's notice runs later.
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray(),
                request => request.Any(message => message.Text == aliceText)) >= 0,
            "The resume must run Alice's interrupted input."), Deadline, cancellationToken: Cancellation);
        var resumed = Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray();
        var aliceRequest = IndexOf(resumed, request => request.Any(message => message.Text == aliceText));
        Assert.False(CarriesChildResult(resumed[aliceRequest], accepted.Run.RunId),
            "Alice's resumed turn must not carry Bob's result.");
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages.Skip(requestsBeforeResume).ToArray(),
                request => CarriesNoticeFor(request, accepted.Run.RunId)) > aliceRequest,
            "Bob's notice must run after Alice's resumed turn."), Deadline, cancellationToken: Cancellation);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Equal(Bob, notice.TurnContext.RequesterSenderId?.Value);
    }
}
