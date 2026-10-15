// -----------------------------------------------------------------------
// <copyright file="WaitingReminderRestartTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: a reminder that waits behind an open approval must survive a restart of the session.
/// <para>
/// The session acknowledges the reminder before it runs. The reminder manager then treats the fire as done and does not
/// send it again. If a restart drops the waiting reminder, nothing runs it. It stays a pending input in the journal.
/// That pending input blocks every later snapshot, and it makes a later restart resume fail on mixed authority.
/// </para>
/// </summary>
public sealed class WaitingReminderRestartTests : ApprovalWaitTestBase
{
    public WaitingReminderRestartTests(ITestOutputHelper output) : base(output)
    {
    }

    // The case without a restart is the control. It proves that the assertions hold when the queue stays in memory.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_waiting_reminder_survives_a_restart_and_runs_once_after_the_approval_closes(
        bool approve,
        bool restartWhileWaiting)
    {
        // Arrange
        var sessionId = new SessionId(
            $"test-channel/restart-keeps-waiting-reminder-{(approve ? "approve" : "deny")}-{(restartWhileWaiting ? "restart" : "live")}");
        var rejoined = await ParkApprovalThenRecoverAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000000");
        if (restartWhileWaiting)
        {
            await StopSessionAsync(sessionId);
            rejoined = await JoinNewSubscriberAsync(sessionId);
        }

        // Act
        Answer(sessionId, approve);

        // Assert: the approval closes first. The reminder then runs as its own turn.
        await ExpectTurnsAsync(rejoined, 1);
        AssertApprovalClosedUnderRequester(approve);
        await ExpectTurnsAsync(rejoined, 1);
        AssertReminderRanOnceAfterApproval();
        await rejoined.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
        await AssertNoInputStaysPendingAsync(sessionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_restart_resume_after_the_waiting_reminder_closed_restores_the_interrupted_input(
        bool restartWhileWaiting)
    {
        // Arrange: the reminder waited, perhaps through a restart, and ran. A person then starts a new turn.
        var sessionId = new SessionId(
            $"test-channel/restart-resume-after-waiting-reminder-{(restartWhileWaiting ? "restart" : "live")}");
        var rejoined = await ParkApprovalThenRecoverAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000001");
        if (restartWhileWaiting)
        {
            await StopSessionAsync(sessionId);
            rejoined = await JoinNewSubscriberAsync(sessionId);
        }

        Answer(sessionId, approve: true);
        await ExpectTurnsAsync(rejoined, 2);
        await AssertNoInputStaysPendingAsync(sessionId);

        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChat.NextResponseGate = responseGate;
        var callsBefore = FakeChat.CallCount;
        await SendAsync(sessionId, "Finish the interrupted task", RequesterSource(Requester));
        await AwaitAssertAsync(
            () =>
            {
                Assert.Equal(callsBefore + 1, FakeChat.CallCount);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);

        // Act: the daemon restarts while the person's turn runs. The restart reminder resumes that turn.
        var drained = await SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        var reminder = Assert.IsType<ReminderDefinition>(drained.RestartReminder);
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var resumed = await JoinNewSubscriberAsync(sessionId);
        var reply = await SessionManager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = reminder.Instructions,
            Source = RestartReminderSource(reminder)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: the pending inputs of the interrupted turn share one authority, so the resume is valid.
        Assert.IsType<CommandAck>(reply);
        await ExpectTurnsAsync(resumed, 1);
        Assert.Contains("Finish the interrupted task", UserTexts(FakeChat.ReceivedMessages[^1]));
    }
}
