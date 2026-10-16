// -----------------------------------------------------------------------
// <copyright file="ChildNoticeCrashOrphanSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Review blocker B1. The session admits a notice, and the actor crashes before the turn of that notice consumes it.
/// The journal then holds an admitted input that nothing consumed. Before S2 a crash after <c>ResultPrepared</c> was
/// safe, because the recovery delivered the prepared result. S2 must keep the rule that a child result is never lost.
/// <para>
/// Scope (owner decision): S2 recovers the orphan notice. A person's orphan input is out of scope (issue #2516).
/// </para>
/// </summary>
public sealed class ChildNoticeCrashOrphanSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// Alice's child ends. The notice turn waits in its first model call, and the actor crashes there. After the
    /// recovery the notice runs once, under the recorded authority of Alice, and a model request carries it.
    /// Planted bug: the recovery rebuilds the waiting inputs only for a parked approval, so the orphan never runs.
    /// </summary>
    [Fact]
    public async Task A_notice_admitted_before_a_crash_runs_once_under_its_recorded_authority()
    {
        // Arrange
        var sessionId = new SessionId("notice/crash-orphan");
        var (journal, accepted, notice) = await CrashWhileTheNoticeTurnWaitsAsync(sessionId);
        var requestsBeforeCrash = Clients.Parent.ReceivedMessages.Count;

        // Act: the recovery runs on the next join.
        await JoinAsync(sessionId);

        // Assert: the notice runs after the crash, once, and the model reads the file under Alice.
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, notice.InputId) > 0,
            "The admitted notice never ran after the crash recovery."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId), "The recovered notice turn did not end."),
            Deadline, cancellationToken: Cancellation);
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Equal(Alice, notice.TurnContext.RequesterSenderId?.Value);
        var recovered = Clients.Parent.ReceivedMessages.Skip(requestsBeforeCrash).ToArray();
        Assert.True(IndexOf(recovered, request => CarriesNoticeFor(request, accepted.Run.RunId)) >= 0,
            "No model request after the crash carries the notice.");
        Assert.Empty(journal.Committed.OfType<ToolBatchAbandoned>());
    }

    /// <summary>
    /// A person writes after the crash. The new message must not leave the orphan notice behind, and it must not run
    /// in one turn with the notice when the person is another requester than the owner of the child.
    /// Planted bug: the activation of a new input selects only the new input, so the orphan stays pending.
    /// </summary>
    [Theory]
    [InlineData(Alice)]
    [InlineData(Bob)]
    public async Task A_later_user_message_does_not_swallow_the_orphan_notice(string sender)
    {
        // Arrange
        var sessionId = new SessionId($"notice/crash-orphan-then-{sender}");
        var (journal, accepted, notice) = await CrashWhileTheNoticeTurnWaitsAsync(sessionId);
        await JoinAsync(sessionId);

        // Act
        await SendAsync(sessionId, "after the crash", Operator(sender));

        // Assert: both inputs run once. The notice came first in admission order, and one turn serves one requester.
        var later = await AwaitInputAdmittedAsync(journal, "after the crash");
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, notice.InputId) > 0,
            "The orphan notice never ran, although a later message ran."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId), "The session did not settle."),
            Deadline, cancellationToken: Cancellation);
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        Assert.Equal(1, ConsumptionCount(journal, later.InputId));
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.True(ConsumedAt(journal, notice.InputId) <= ConsumedAt(journal, later.InputId),
            "The notice was admitted first, so it must not run after the later message.");
        if (sender == Bob)
        {
            var noticeTurn = journal.Committed.ToArray().Single(evt => ConsumedBy(evt).Contains(notice.InputId));
            Assert.DoesNotContain(later.InputId, ConsumedBy(noticeTurn));
        }
    }

    /// <summary>
    /// Re-review NS1. In production the message of a person is the message that starts the recovered actor. No join
    /// comes first. The message waits in the recovery stash, and the orphan notice was admitted before it. The notice
    /// must run first, and the message must wait behind it.
    /// Planted bug: the recovery resumes the orphan notice with a message to self after the stash. The stashed message
    /// then activates first, and the notice runs in the next turn.
    /// </summary>
    [Theory]
    [InlineData(Alice)]
    [InlineData(Bob)]
    public async Task A_message_that_wakes_the_recovered_session_runs_after_the_orphan_notice(string sender)
    {
        // Arrange
        var sessionId = new SessionId($"notice/crash-orphan-wake-by-{sender}");
        var (journal, accepted, notice) = await CrashWhileTheNoticeTurnWaitsAsync(sessionId);

        // Act: no join. The message starts the actor, so it waits in the recovery stash.
        await SendAsync(sessionId, "after the crash", Operator(sender));

        // Assert
        var later = await AwaitInputAdmittedAsync(journal, "after the crash");
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, notice.InputId) > 0,
            "The orphan notice never ran."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, later.InputId) > 0,
            "The message that woke the session never ran."), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId), "The session did not settle."),
            Deadline, cancellationToken: Cancellation);
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        Assert.Equal(1, ConsumptionCount(journal, later.InputId));
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.True(ConsumedAt(journal, notice.InputId) <= ConsumedAt(journal, later.InputId),
            $"The notice was admitted first, but the message that woke the session ran first "
            + $"(notice at {ConsumedAt(journal, notice.InputId)}, message at {ConsumedAt(journal, later.InputId)}).");
        if (sender == Bob)
        {
            var noticeTurn = journal.Committed.ToArray().Single(evt => ConsumedBy(evt).Contains(notice.InputId));
            Assert.DoesNotContain(later.InputId, ConsumedBy(noticeTurn));
        }
    }
}

/// <summary>
/// Review blocker B1, third part. An orphan input blocks every snapshot, because a snapshot would drop the input
/// that nothing consumed ("Skipping snapshot while a durable input or approval batch is unresolved"). Once the recovery
/// resumes the notice, snapshots must work again.
/// </summary>
public sealed class ChildNoticeOrphanSnapshotSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    protected override int SnapshotInterval => 1;

    /// <summary>
    /// After the crash and the recovery, a message of Alice runs, and then the session saves a snapshot that is newer
    /// than the admission of the notice.
    /// Planted bug: the orphan notice stays pending, so no snapshot after the crash passes the guard.
    /// </summary>
    [Fact]
    public async Task An_orphan_notice_does_not_block_snapshots_after_the_recovery()
    {
        // Arrange
        var sessionId = new SessionId("notice/crash-orphan-snapshot");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var hold = NewGate();
        Clients.Parent.NextResponseGate = hold;
        gate.TrySetResult();
        await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(() => Assert.Null(Clients.Parent.NextResponseGate), Deadline, cancellationToken: Cancellation);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        await StopAsync(await ResolveSessionAsync(sessionId));
        hold.TrySetResult();

        // Act: the recovery, and then one more message that gives the session a write to snapshot.
        await JoinAsync(sessionId);
        await SendAsync(sessionId, "after the crash", Operator(Alice));
        var later = await AwaitInputAdmittedAsync(journal, "after the crash");
        await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, later.InputId) > 0,
            "The message after the crash never ran."), Deadline, cancellationToken: Cancellation);

        // Assert: a snapshot exists that is newer than the notice admission.
        var noticeSequence = await SequenceOfAsync(sessionId, notice.InputId);
        await AwaitAssertAsync(async () =>
        {
            var snapshot = await LatestSnapshotSequenceNrAsync(sessionId);
            Assert.True(snapshot > noticeSequence,
                $"The latest snapshot ({snapshot}) is not newer than the admission of the notice ({noticeSequence}). "
                + "An unconsumed notice blocks every snapshot.");
        }, Deadline, cancellationToken: Cancellation);
    }
}
