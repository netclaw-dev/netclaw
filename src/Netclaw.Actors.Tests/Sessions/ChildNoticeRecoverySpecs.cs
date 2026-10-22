// -----------------------------------------------------------------------
// <copyright file="ChildNoticeRecoverySpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for results that must survive a crash, a passivation, and a daemon restart.
/// A terminal child run yields one notice, even when the actor stops between <c>TerminalRecorded</c> and the notice.
/// </summary>
public sealed class ChildNoticeRecoverySpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// Spec 10. The actor crashes after <c>TerminalRecorded</c> and before the notice. The recovery writes the result file
    /// and admits one notice for the run.
    /// Planted bug: the recovery skips runs in the terminal state.
    /// </summary>
    [Fact]
    public async Task A_terminal_run_without_a_notice_is_completed_on_recovery()
    {
        // Arrange: the preparation of the result is held, so the journal ends at TerminalRecorded.
        var sessionId = new SessionId("notice/recover-terminal");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        // The spec never releases this hold, because the recovered session writes its own result file. A released
        // write of the stopped session could land after the cleanup of the temp folder and create the folder again.
        Preparation.HoldNext();
        gate.TrySetResult();
        await Preparation.Entered.Task.WaitAsync(Deadline, Cancellation);
        Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
        Assert.Empty(NoticesFor(journal, accepted.Run.RunId));

        // Act
        var recovered = await RestartSessionAsync(sessionId);

        // Assert
        var resultPath = ResultPath(sessionId, accepted);
        await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count > 0,
            "The recovery must admit a notice for the terminal run."), Deadline, cancellationToken: Cancellation);
        await ExpectTurnsAsync(recovered, 1);
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.True(File.Exists(resultPath), $"The recovery must write {resultPath}.");
        Assert.Contains(ChildOutput, await File.ReadAllTextAsync(resultPath, Cancellation), StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 11. The idle timeout arrives while the write of the result is held. The session stays in memory, so the
    /// notice runs after the release, with no new join to wake the session.
    /// Planted bug: the passivation guard ignores a terminal run that has no notice.
    /// </summary>
    [Fact]
    public async Task Session_stays_while_a_terminal_child_has_no_notice()
    {
        // Arrange
        var sessionId = new SessionId("notice/stays-for-terminal");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        Preparation.HoldNext();

        try
        {
            gate.TrySetResult();
            await Preparation.Entered.Task.WaitAsync(Deadline, Cancellation);
            var session = await ResolveSessionAsync(sessionId);

            // Act: the idle timeout reaches the session before the release, in mailbox order.
            session.Tell(ReceiveTimeout.Instance);
            Preparation.Release.TrySetResult();

            // Assert: the session wrote the notice itself. A passivated session cannot do so until a new join.
            await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count == 1,
                "The session must stay for a terminal run and admit its notice after the release."),
                Deadline, cancellationToken: Cancellation);
            await ExpectTurnsAsync(subscriber, 1);
        }
        finally
        {
            Preparation.Release.TrySetResult();
        }
    }

    /// <summary>
    /// Spec 12 (S8). A session holds only a live child and no pending input. The daemon restart drain returns a restart
    /// reminder that carries the recorded context of the child. After the restart the reminder wakes the session, and
    /// one notice reports the lost child.
    /// Planted bug: the eligibility check keeps the rule "no pending input, no reminder".
    /// </summary>
    [Fact]
    public async Task Child_only_session_gets_a_restart_resume_and_reports_the_lost_child()
    {
        // Arrange
        var sessionId = new SessionId("notice/child-only-restart");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var session = await ResolveSessionAsync(sessionId);
        var stopped = CreateTestProbe();
        stopped.Watch(session);

        // Act: the daemon drains the idle session while the child runs.
        var drained = await AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));

        // Assert: the drain asks for a restart reminder, and the reminder uses the recorded context of the child.
        var reminder = drained.RestartReminder;
        if (reminder is null)
            Assert.Fail("The drain returned no restart reminder, but a live child needs one.");
        Assert.Equal(TrustAudience.Personal, reminder.Audience);
        Assert.Equal(TrustBoundary.Personal, reminder.Boundary);
        Assert.Equal(sessionId.Value, reminder.Delivery.SessionId);
        Assert.Equal(ChannelType.SignalR, reminder.Delivery.OriginChannelType);
        await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);

        // After the restart the reminder system sends the reminder to the session. Then one notice reports the lost child.
        var fire = reminder.Schedule.FireAt ?? throw new InvalidOperationException("The restart reminder has no fire time.");
        await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = reminder.Instructions,
            Source = Reminder($"{reminder.Id.Value}:{fire.ToUnixTimeMilliseconds()}")
        });
        await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count == 1,
            "The restart must yield one notice for the lost child."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(() => Assert.True(
            IndexOf(Clients.Parent.ReceivedMessages, request => CarriesNoticeFor(request, accepted.Run.RunId)) >= 0,
            "A model request must carry the notice of the lost child."), Deadline, cancellationToken: Cancellation);
        var resultPath = ResultPath(sessionId, accepted);
        await AwaitAssertAsync(() => Assert.True(File.Exists(resultPath), $"The runtime must write {resultPath}."),
            Deadline, cancellationToken: Cancellation);
        // Recovery cannot tell a restart drain from a crash, so it names the fact that it knows (issue 2602).
        Assert.Contains("- reason: owner_restarted", await File.ReadAllTextAsync(resultPath, Cancellation), StringComparison.Ordinal);
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
    }

    /// <summary>
    /// Spec 15. New writes never set the legacy continuation field and never write <c>ResultPrepared</c>.
    /// Two children of two requesters end and reach the model. No committed event is a <c>ResultPrepared</c> event,
    /// and no committed event carries a child continuation. The names are read by reflection, because S2 keeps the legacy
    /// field as read-only data and may keep the <c>ResultPrepared</c> type for the reader.
    /// Planted bug: a writer sets the legacy field or writes <c>ResultPrepared</c>.
    /// </summary>
    [Fact]
    public async Task New_writes_never_set_legacy_continuation_or_result_prepared()
    {
        // Arrange
        var sessionId = new SessionId("notice/no-legacy-writes");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (alice, aliceGate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var (bob, bobGate) = await StartChildAsync(sessionId, Bob, "two", agent: "child-b");
        await ExpectTurnsAsync(subscriber, 1);

        // Act
        aliceGate.TrySetResult();
        bobGate.TrySetResult();
        await AwaitAssertAsync(() =>
        {
            var requests = Clients.Parent.ReceivedMessages;
            Assert.True(IndexOf(requests, request => CarriesChildResult(request, alice.Run.RunId)) >= 0, "Alice's result is not delivered.");
            Assert.True(IndexOf(requests, request => CarriesChildResult(request, bob.Run.RunId)) >= 0, "Bob's result is not delivered.");
        }, Deadline, cancellationToken: Cancellation);
        await SendAsync(sessionId, "ping", Operator(Alice));
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<TurnRecorded>(),
            evt => evt.UserMessage.Content == "ping"), Deadline, cancellationToken: Cancellation);

        // Assert
        var events = journal.Committed.ToArray();
        var prepared = events.Where(evt => evt.GetType().Name == "ResultPrepared").ToArray();
        var continued = events
            .Where(evt => evt.GetType().GetProperty("ChildContinuation")?.GetValue(evt) is not null)
            .Select(evt => evt.GetType().Name)
            .ToArray();
        Note($"ResultPrepared events: {prepared.Length}; events with a child continuation: [{string.Join(", ", continued)}]");
        Assert.True(prepared.Length == 0, $"The runtime wrote {prepared.Length} ResultPrepared event(s).");
        Assert.True(continued.Length == 0, $"These events carry a child continuation: [{string.Join(", ", continued)}].");
    }
}
