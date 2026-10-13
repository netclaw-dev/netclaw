// -----------------------------------------------------------------------
// <copyright file="SessionExecutionLifetimeStopPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionExecutionLifetimeStopPolicyTests
{
    [Fact]
    public void Superseded_reap_reply_cannot_release_the_current_passivation()
    {
        // Arrange: the first passivation waits for a manager reply.
        var owner = CreateOwner();
        var oldEpoch = owner.BeginPassivation(true, true, false).ReapEpoch;
        Assert.NotNull(oldEpoch);
        Assert.Equal(PassivationWork.WaitForReap, owner.CompletePassivationWork(SessionPhase.Passivating));

        // Act: an abort and a new passivation supersede the old reply.
        owner.AbortPassivation();
        var currentEpoch = owner.BeginPassivation(true, true, false).ReapEpoch;
        Assert.NotNull(currentEpoch);
        Assert.NotEqual(oldEpoch, currentEpoch);
        Assert.Equal(PassivationWork.WaitForReap, owner.CompletePassivationWork(SessionPhase.Passivating));
        var acceptsOldReply = owner.ObserveReapReply(oldEpoch.Value);
        var oldCommitWork = owner.ReapCommitted(oldEpoch.Value, SessionPhase.Passivating);

        // Assert: the old reply cannot request a journal write or release the new stop.
        Assert.False(acceptsOldReply);
        Assert.Equal(PassivationWork.NoAction, oldCommitWork);
        Assert.False(owner.IsCurrentFinalStop(SessionPhase.Passivating));
        Assert.Null(owner.ClaimFinalStop(SessionPhase.Passivating, new SessionId("test/current-reap"), []));

        // Act: the current reply reaches the owner twice before its journal commit.
        var acceptsCurrentReply = owner.ObserveReapReply(currentEpoch.Value);
        var acceptsDuplicateBeforeCommit = owner.ObserveReapReply(currentEpoch.Value);
        var workBeforeCommit = owner.CompletePassivationWork(SessionPhase.Passivating);
        var finalBeforeCommit = owner.ClaimFinalStop(
            SessionPhase.Passivating, new SessionId("test/current-reap"), []);

        // Assert: an accepted reply still cannot bypass its journal commit.
        Assert.True(acceptsCurrentReply);
        Assert.False(acceptsDuplicateBeforeCommit);
        Assert.Equal(PassivationWork.WaitForReap, workBeforeCommit);
        Assert.Null(finalBeforeCommit);

        // Act: the journal confirms the accepted reply.
        var currentCommitWork = owner.ReapCommitted(currentEpoch.Value, SessionPhase.Passivating);
        var acceptsDuplicateAfterCommit = owner.ObserveReapReply(currentEpoch.Value);
        var duplicateCommitWork = owner.ReapCommitted(currentEpoch.Value, SessionPhase.Passivating);

        // Assert: one current reply requests one commit and one final snapshot.
        Assert.Equal(PassivationWork.AttemptSnapshot, currentCommitWork);
        Assert.False(acceptsDuplicateAfterCommit);
        Assert.Equal(PassivationWork.NoAction, duplicateCommitWork);
    }

    [Fact]
    public void Reap_commit_after_abort_preserves_the_reap_without_a_stop_claim()
    {
        // Arrange: passivation waits for a valid reap reply.
        var owner = CreateOwner();
        var epoch = owner.BeginPassivation(true, true, false).ReapEpoch;
        Assert.NotNull(epoch);
        Assert.Equal(PassivationWork.WaitForReap, owner.CompletePassivationWork(SessionPhase.Passivating));

        // Act: input aborts passivation before the manager reply and journal commit.
        owner.AbortPassivation();
        var persistReap = owner.ObserveReapReply(epoch.Value);
        var laterWork = owner.ReapCommitted(epoch.Value, SessionPhase.Ready);
        var final = owner.ClaimFinalStop(SessionPhase.Ready, new SessionId("test/aborted-reap"), []);

        // Assert: the valid reap still commits, but it cannot finish the aborted stop.
        Assert.True(persistReap);
        Assert.Equal(PassivationWork.NoAction, laterWork);
        Assert.Null(final);
        Assert.True(owner.NeedsAbruptStopNotification);
    }

    [Fact]
    public void Abort_before_final_claim_cancels_the_stop_decision()
    {
        // Arrange: a passivation reaches the final grace window.
        var owner = CreateOwner();
        owner.BeginPassivation(false, false, false);
        Assert.Equal(PassivationWork.AttemptSnapshot, owner.CompletePassivationWork(SessionPhase.Passivating));
        Assert.False(owner.AfterFinalSnapshotAttempt(SessionPhase.Passivating));
        Assert.True(owner.IsCurrentFinalStop(SessionPhase.Passivating));

        // Act: input aborts passivation before the final claim.
        owner.AbortPassivation();
        var final = owner.ClaimFinalStop(SessionPhase.Passivating, new SessionId("test/aborted-stop"), []);

        // Assert: the old timer cannot claim a stop or suppress abrupt-stop notice.
        Assert.False(owner.IsCurrentFinalStop(SessionPhase.Passivating));
        Assert.Null(final);
        Assert.True(owner.NeedsAbruptStopNotification);
    }

    [Fact]
    public void Final_claim_remains_complete_after_resources_are_released()
    {
        // Arrange: a passivation reaches the final grace window.
        var owner = CreateOwner();
        var sessionId = new SessionId("test/final-claim");
        owner.BeginPassivation(false, false, false);
        Assert.Equal(PassivationWork.AttemptSnapshot, owner.CompletePassivationWork(SessionPhase.Passivating));
        Assert.False(owner.AfterFinalSnapshotAttempt(SessionPhase.Passivating));

        // Act: the session claims final effects and releases model and tool resources.
        var final = owner.ClaimFinalStop(SessionPhase.Passivating, sessionId, []);
        owner.ReleaseAll();
        var duplicate = owner.ClaimFinalStop(SessionPhase.Passivating, sessionId, []);

        // Assert: PostStop must not request a second abrupt-stop notice.
        Assert.NotNull(final);
        Assert.Null(duplicate);
        Assert.False(owner.NeedsAbruptStopNotification);
    }

    private static SessionExecutionLifetime CreateOwner()
        => new(new ProcessingWatchdogTests.RecordingTimerScheduler(), ActorRefs.Nobody);
}
