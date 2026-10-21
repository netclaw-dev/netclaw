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
}
