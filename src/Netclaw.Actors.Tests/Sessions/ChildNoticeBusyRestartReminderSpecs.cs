// -----------------------------------------------------------------------
// <copyright file="ChildNoticeBusyRestartReminderSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Re-review NS2. The recovery owns the resume of the work that a restart interrupted. A restart reminder that arrives
/// after the recovery started a turn must not become an ordinary input. If it did, the model would get the order
/// "Resume the work that was interrupted by the daemon restart." as new work, and it could repeat work (and the
/// external effects of that work) that the recovery resumed already.
/// </summary>
public sealed class ChildNoticeBusyRestartReminderSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// The actor crashed with an unconsumed notice. A join starts the actor, and the recovery runs the notice turn.
    /// The turn waits in its first model call. The restart reminder arrives in that turn.
    /// Planted bug: the busy session admits every message that is not a duplicate, so the reminder text becomes an input
    /// and a second turn.
    /// </summary>
    [Fact]
    public async Task A_restart_reminder_during_a_recovered_notice_turn_is_not_admitted_as_input()
    {
        // Arrange
        var sessionId = new SessionId("notice/reminder-in-recovered-turn");
        var (journal, _, notice) = await CrashWhileTheNoticeTurnWaitsAsync(sessionId);
        var hold = NewGate();
        Clients.Parent.NextResponseGate = hold;
        await JoinAsync(sessionId);
        await AwaitAssertAsync(() => Assert.Null(Clients.Parent.NextResponseGate), Deadline, cancellationToken: Cancellation);

        // Act: the notice turn is in its first model call when the reminder arrives.
        var reply = await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = SessionExecutionLifetime.RestartReminderInstruction,
            Source = Reminder("restart-resume-notice-turn:1")
        });
        Note($"reminder reply: {reply.GetType().Name}");
        AssertNotAdmitted(journal);
        hold.TrySetResult();

        // Assert
        await AssertNoticeRanOnceAndNoReminderTurnAsync(sessionId, journal, notice);
    }

    /// <summary>
    /// Alice's child is alive when the daemon drains the idle session. After the restart a join starts the actor, the
    /// recovery ends the lost child, and the notice turn waits in its first model call. The restart reminder arrives in
    /// that turn. The race exists at the base of the stack, because the busy session never knew the reminder.
    /// Planted bug: as in the spec above.
    /// </summary>
    [Fact]
    public async Task A_restart_reminder_during_a_lost_child_notice_turn_is_not_admitted_as_input()
    {
        // Arrange
        var sessionId = new SessionId("notice/reminder-in-lost-child-turn");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        var drained = await AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));
        var reminder = drained.RestartReminder
            ?? throw new InvalidOperationException("The drain returned no restart reminder, but a live child needs one.");
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);

        var hold = NewGate();
        Clients.Parent.NextResponseGate = hold;
        await JoinAsync(sessionId);
        await AwaitAssertAsync(() => Assert.Null(Clients.Parent.NextResponseGate), Deadline, cancellationToken: Cancellation);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));

        // Act: the notice turn of the lost child is in its first model call when the reminder arrives.
        var reply = await SendRestartReminderAsync(sessionId, reminder);
        Note($"reminder reply: {reply.GetType().Name}");
        AssertNotAdmitted(journal, reminder.Instructions);
        hold.TrySetResult();

        // Assert
        await AssertNoticeRanOnceAndNoReminderTurnAsync(sessionId, journal, notice, reminder.Instructions);
    }

    /// <summary>
    /// The restart reminder is the message that starts the crashed actor. Whichever message the recovery handles first,
    /// the notice runs once, and the reminder text never becomes an input. This spec guards the order of the recovery:
    /// if the recovery resumes the notice before the stashed reminder, the reminder meets a busy session.
    /// Planted bug: the reminder resumes the pending notice, and the recovery resumes it again.
    /// </summary>
    [Fact]
    public async Task A_restart_reminder_that_wakes_the_crashed_session_runs_the_notice_once()
    {
        // Arrange
        var sessionId = new SessionId("notice/reminder-wakes-crashed-session");
        var (journal, _, notice) = await CrashWhileTheNoticeTurnWaitsAsync(sessionId);

        // Act: no join. The reminder starts the actor.
        var reply = await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = SessionExecutionLifetime.RestartReminderInstruction,
            Source = Reminder("restart-resume-wakes-session:1")
        });
        Note($"reminder reply: {reply.GetType().Name}");

        // Assert
        await AssertNoticeRanOnceAndNoReminderTurnAsync(sessionId, journal, notice);
    }

    private static void AssertNotAdmitted(AcceptanceFault journal, string? text = null)
    {
        var instruction = text ?? SessionExecutionLifetime.RestartReminderInstruction;
        var admitted = journal.Committed.OfType<InputAdmitted>()
            .Where(input => input.UserMessage.Content == instruction)
            .ToArray();
        Assert.True(admitted.Length == 0,
            $"The busy session admitted the restart reminder as {admitted.Length} new input(s): \"{instruction}\"");
    }

    private async Task AssertNoticeRanOnceAndNoReminderTurnAsync(
        SessionId sessionId, AcceptanceFault journal, InputAdmitted notice, string? text = null)
    {
        var instruction = text ?? SessionExecutionLifetime.RestartReminderInstruction;
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, notice.InputId) > 0,
            "The notice never ran."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId), "The session did not settle."),
            Deadline, cancellationToken: Cancellation);
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        AssertNotAdmitted(journal, instruction);
        var asked = Clients.Parent.ReceivedMessages.Count(request => request.Any(message =>
            message.Role == Microsoft.Extensions.AI.ChatRole.User
            && message.Text.Contains(instruction, StringComparison.Ordinal)));
        Assert.True(asked == 0, $"{asked} model request(s) carried the restart reminder text as user input.");
    }
}
