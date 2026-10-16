// -----------------------------------------------------------------------
// <copyright file="ChildNoticeFailurePathSpecs.cs" company="Petabridge, LLC">
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
/// Review S6, first part. The write of <c>result.md</c> fails. The parent must still learn that the run ended, and the
/// notice must say that the file is missing. The failure is logged at Error level. The run never stays without a notice.
/// </summary>
public sealed class ChildNoticeResultWriteFailureSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// A directory sits at the path of <c>result.md</c>, so the rename of the temporary file fails. The session logs the
    /// failure, and it admits one notice that says the runtime could not write the file. The notice does not tell the
    /// model to read the file, and it does not carry the child output. After a crash and a recovery the run gets no
    /// second notice, because the run is reported.
    /// Planted bug: the catch around the write is narrow, so the failure ends the preparation task unobserved and the
    /// run has no notice. Or the notice text claims that the file is ready.
    /// </summary>
    [Fact]
    public async Task A_failed_result_write_yields_one_notice_that_says_the_file_is_missing_and_logs_an_error()
    {
        // Arrange
        var sessionId = new SessionId("notice/result-write-fails");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var resultPath = ResultPath(sessionId, accepted);
        Directory.CreateDirectory(resultPath);

        // Act
        await EventFilter.Error(contains: "could not be written").ExpectAsync(1, async () =>
        {
            gate.TrySetResult();
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
                Deadline, cancellationToken: Cancellation);
        }, cancellationToken: Cancellation);

        // Assert: one notice, and its text says that the file is missing.
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        var text = notice.UserMessage.Content;
        Assert.Contains("could not write the result file", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Read the result file with file_read", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ChildOutput, text, StringComparison.Ordinal);
        Assert.False(File.Exists(resultPath), "The failed write must leave no result file.");
        await ExpectTurnsAsync(subscriber, 1);

        // The run is reported: a crash and a recovery add no second notice. A later message proves the recovery ended.
        await RestartSessionAsync(sessionId);
        await SendAsync(sessionId, "after the failed write", Operator(Alice));
        var later = await AwaitInputAdmittedAsync(journal, "after the failed write");
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, later.InputId) > 0,
            "The message after the recovery never ran."), Deadline, cancellationToken: Cancellation);
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
    }
}

/// <summary>
/// Review S6, second part. The reducer refuses the notice of an ended run. A retry cannot succeed, so the session logs
/// the reason and the path of the result file at Error level, and it stops counting the run as owed. The run must not
/// pin the session in memory, and it must not drive the restart reminder.
/// <para>
/// The session produces a refusable notice only when the recorded authority of a run is damaged, and the journal
/// rejects a damaged run before that point. The specs make the refusal with a notice that has the wrong authority.
/// The test sends it to the session while the write of <c>result.md</c> is held. The run is then ended, and no valid
/// notice exists yet. This is the same reducer step and the same owner code that a damaged record would reach.
/// </para>
/// </summary>
public sealed class ChildNoticeRefusedNoticeSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    private const string WrongAuthority = "A child notice must carry the recorded authority of its run owner";

    /// <summary>
    /// The log names the reason and the path of <c>result.md</c> once.
    /// Planted bug: the owner drops the refusal without a log, or the log hides the reason or the path.
    /// </summary>
    [Fact]
    public async Task A_notice_that_the_reducer_refuses_is_logged_with_its_reason_and_the_result_path()
    {
        // Arrange
        var sessionId = new SessionId("notice/refused-logged");
        var (journal, accepted, session) = await HoldEndedRunAsync(sessionId);

        try
        {
            // Act: the filter counts the lines that name the path, and the probe reads the text of the line.
            var resultPath = ResultPath(sessionId, accepted);
            var errors = CreateTestProbe();
            Sys.EventStream.Subscribe(errors.Ref, typeof(Akka.Event.Error));
            await EventFilter.Error(contains: $"The result file is {resultPath}").ExpectAsync(1,
                async () => await RefuseNoticeAsync(session, accepted), cancellationToken: Cancellation);

            // Assert: the line names the run, the real reason, and the path.
            var line = await errors.FishForMessageAsync<Akka.Event.Error>(
                error => error.Message.ToString()!.Contains("cannot report child run", StringComparison.Ordinal),
                Deadline, cancellationToken: Cancellation);
            var text = line.Message.ToString()!;
            Assert.Contains(accepted.Run.RunId.Value, text, StringComparison.Ordinal);
            Assert.Contains(WrongAuthority, text, StringComparison.Ordinal);
            Assert.Contains(resultPath, text, StringComparison.Ordinal);

            // The refusal wrote nothing to the journal.
            Assert.Empty(NoticesFor(journal, accepted.Run.RunId));
        }
        finally
        {
            Preparation.Release.TrySetResult();
        }
    }

    /// <summary>
    /// After the refusal the run does not pin the session. The idle timeout stops the session. The contrast is spec 11:
    /// an ended run that is still owed keeps the session in memory.
    /// Planted bug: the refusal leaves the run in the set of runs that the parent is owed.
    /// </summary>
    [Fact]
    public async Task A_refused_notice_does_not_keep_the_session_in_memory()
    {
        // Arrange
        var sessionId = new SessionId("notice/refused-no-pin");
        var (_, accepted, session) = await HoldEndedRunAsync(sessionId);

        try
        {
            await RefuseNoticeAsync(session, accepted);
            var stopped = CreateTestProbe();
            stopped.Watch(session);

            // Act: the idle timeout reaches an idle session.
            session.Tell(ReceiveTimeout.Instance);

            // Assert
            await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        }
        finally
        {
            Preparation.Release.TrySetResult();
        }
    }

    /// <summary>
    /// After the refusal the run does not drive the restart reminder. The drain of an idle session returns no reminder,
    /// because nothing is owed to the parent. The contrast is spec 12: a live child drives a reminder.
    /// Planted bug: the refusal leaves the run in the set of runs that the parent is owed.
    /// </summary>
    [Fact]
    public async Task A_refused_notice_does_not_drive_the_restart_reminder()
    {
        // Arrange
        var sessionId = new SessionId("notice/refused-no-reminder");
        var (_, accepted, session) = await HoldEndedRunAsync(sessionId);

        try
        {
            await RefuseNoticeAsync(session, accepted);

            // Act
            var drained = await AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "daemon-stop"));

            // Assert
            Assert.Null(drained.RestartReminder);
        }
        finally
        {
            Preparation.Release.TrySetResult();
        }
    }

    /// <summary>
    /// Negative example. A second notice for a run that has its notice already is refused, but the refusal is not a lost
    /// result: the run is reported. The session writes no "cannot report" line for it.
    /// Planted bug: the owner gives up on every refusal, whatever the state of the run.
    /// </summary>
    [Fact]
    public async Task A_duplicate_notice_is_refused_without_a_give_up_log()
    {
        // Arrange
        var sessionId = new SessionId("notice/refused-duplicate");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        gate.TrySetResult();
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)),
            Deadline, cancellationToken: Cancellation);
        await ExpectTurnsAsync(subscriber, 1);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        var session = await ResolveSessionAsync(sessionId);
        var duplicate = new SendUserMessage
        {
            SessionId = sessionId,
            Content = notice.UserMessage.Content,
            Notice = new ChildNoticeInput(new ChildNotice(accepted.Run.RunId, null), notice.TurnContext with { TurnId = "duplicate-notice" })
        };

        // Act: the Nack is the barrier. The owner logs the refusal before it replies.
        await EventFilter.Error(contains: "The result file is").ExpectAsync(0, async () =>
        {
            var reply = await session.Ask<ISessionResponse>(duplicate, TimeSpan.FromHours(1), Cancellation).WaitAsync(Deadline, Cancellation);
            Assert.IsType<CommandNack>(reply);
        }, cancellationToken: Cancellation);

        // Assert
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
    }

    // Alice's child ends, and the write of its result file is held. The run is ended and owes its notice.
    private async Task<(AcceptanceFault Journal, ChildRunAccepted Accepted, IActorRef Session)> HoldEndedRunAsync(SessionId sessionId)
    {
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        Preparation.HoldNext();
        gate.TrySetResult();
        await Preparation.Entered.Task.WaitAsync(Deadline, Cancellation);
        Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
        Assert.Empty(NoticesFor(journal, accepted.Run.RunId));
        return (journal, accepted, await ResolveSessionAsync(sessionId));
    }

    // A notice that names Bob as the requester of Alice's run. The reducer refuses it.
    private async Task RefuseNoticeAsync(IActorRef session, ChildRunAccepted accepted)
    {
        var wrong = accepted.Run.OriginalContext with { RequesterSenderId = new SenderId(Bob), TurnId = "wrong-authority-notice" };
        var reply = await session.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = accepted.Run.OriginalContext.SessionId,
            Content = "Child run ended.",
            Notice = new ChildNoticeInput(new ChildNotice(accepted.Run.RunId, null), wrong)
        }, TimeSpan.FromHours(1), Cancellation).WaitAsync(Deadline, Cancellation);
        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Contains(WrongAuthority, nack.Reason, StringComparison.Ordinal);
    }
}
