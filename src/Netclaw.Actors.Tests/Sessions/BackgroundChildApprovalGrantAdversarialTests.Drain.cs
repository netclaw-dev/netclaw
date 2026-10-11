// -----------------------------------------------------------------------
// <copyright file="BackgroundChildApprovalGrantAdversarialTests.Drain.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Persistence;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildApprovalGrantAdversarialTests
{
    [Fact]
    public async Task Coordinated_drain_commits_the_live_child_prompt_disposition_before_terminal_snapshot_and_acknowledgement()
    {
        var gate = InstallWaitReplyGate();
        try
        {
            var (manager, _, prompt) = await StartPromptAsync();
            var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(Session.Value)}")
                .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            var admission = Assert.Single(before.OfType<ChildRunAccepted>());
            var requested = Assert.Single(before.OfType<ToolApprovalRequested>());
            Assert.Equal(admission.Run.RunId, requested.SourceChildRunId);
            Assert.Equal(prompt.CallId.Value, requested.CallId);
            Assert.Equal(prompt.AuthorizationAttemptId, requested.AuthorizationAttemptId);
            Assert.Equal(new SenderId("operator-a"), requested.RequesterSenderId);
            Assert.Equal(new ToolCallId("child-approval-1"), requested.OriginalChildCallId);
            Assert.Empty(before.OfType<ToolApprovalResolved>());
            Assert.Empty(before.OfType<ChildRunEvent.TerminalRecorded>());
            var child = await Sys.ActorSelection($"{owner.Path}/child-run-{admission.Run.RunId.Value}")
                .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
            var ownerWatcher = CreateTestProbe();
            var childWatcher = CreateTestProbe();
            ownerWatcher.Watch(owner);
            childWatcher.Watch(child);
            var drain = manager.Ask<DaemonRestartPrepared>(new PrepareForDaemonRestart(Session, "daemon-stop"),
                Ceiling, TestContext.Current.CancellationToken);
            var ack = await drain;
            Assert.Equal(Session, ack.SessionId);
            Assert.Null(ack.RestartReminder);
            await ownerWatcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            await childWatcher.ExpectTerminatedAsync(child, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            SessionSnapshot? snapshot = null;
            await AwaitAssertAsync(async () =>
            {
                snapshot = await ReadPromptDrainSnapshotAsync();
                Assert.NotNull(snapshot);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var run = Assert.Single(snapshot!.ChildRuns);
            run.Validate();
            Assert.Equal(admission.Run.RunId, run.RunId);
            Assert.Equal(BackgroundChildState.Cancelled, run.State);
            Assert.NotNull(run.CancellationRequestedAtMs);
            Assert.NotNull(run.DispatchClosedAtMs);
            Assert.True(run.TerminalSequenceNr > 0);
            Assert.False(run.Terminal!.Lost);
            Assert.IsType<ChildRunCompletion.Cancelled>(run.Terminal.Result.Completion);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, run.Terminal.Result.OutcomeReason);
            Assert.False(run.Terminal.Result.Success);
            var approval = Assert.Single(run.Approvals);
            Assert.Equal(requested.CallId, approval.Request.CallId);
            Assert.Equal(requested.AuthorizationAttemptId, approval.Request.AuthorizationAttemptId);
            Assert.Equal(requested.RequesterSenderId, approval.Request.RequesterSenderId);
            Assert.Equal(requested.OriginalChildCallId, approval.Request.OriginalChildCallId);
            Assert.NotNull(approval.Resolution);
            Assert.Equal(admission.Run.RunId, approval.Resolution.SourceChildRunId);
            Assert.Equal(requested.CallId, approval.Resolution.CallId);
            Assert.Equal(requested.AuthorizationAttemptId, approval.Resolution.AuthorizationAttemptId);
            Assert.Equal("Denied", approval.Resolution.Decision);
            Assert.DoesNotContain(run.Approvals, item => item.Resolution is null);
            Assert.Equal(0, Volatile.Read(ref _approvedEffects));
            Assert.Empty(_store.Load().Audiences);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(2, _main.CallCount);
        }
        finally
        {
            ReleaseOwnedSignals(gate);
        }
    }

    private async Task<SessionSnapshot?> ReadPromptDrainSnapshotAsync()
    {
        var done = new TaskCompletionSource<SessionSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new PromptDrainSnapshotReader($"session-{Session.Value}", done)));
        var watcher = CreateTestProbe();
        watcher.Watch(reader);
        try { return await done.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken); }
        finally
        {
            Sys.Stop(reader);
            await watcher.ExpectTerminatedAsync(reader, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private sealed class PromptDrainSnapshotReader : ReceivePersistentActor
    {
        public PromptDrainSnapshotReader(string persistenceId, TaskCompletionSource<SessionSnapshot?> done)
        {
            PersistenceId = persistenceId;
            SessionSnapshot? snapshot = null;
            Recover<SnapshotOffer>(offer =>
            {
                snapshot = Assert.IsType<SessionSnapshot>(offer.Snapshot);
                _ = SessionState.FromSnapshot(snapshot);
            });
            Recover<ISessionEvent>(_ => { });
            Recover<RecoveryCompleted>(_ => done.TrySetResult(snapshot));
        }
        public override string PersistenceId { get; }
    }
}
