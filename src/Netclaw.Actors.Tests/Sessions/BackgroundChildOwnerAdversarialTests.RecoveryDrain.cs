// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.RecoveryDrain.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Persistence;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cold_recovery_preserves_committed_cancellation_before_its_terminal(bool closureCommitted)
    {
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Delegate the neutral task.",
            Source = DeliverySource("original", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        var entered = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            var selected = closureCommitted
                ? record.Payload is ChildRunEvent.TerminalRecorded
                : record.Payload is ChildRunEvent.DispatchClosed;
            if (!selected) return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        });
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            _main.PlannedResponses.Enqueue([ControlCall("cancel-before-recovery", accepted.Run.RunId)]);
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = Session, Content = "Cancel the accepted child.",
                Source = DeliverySource("cancel-before-recovery", "operator-a")
            }, Ceiling, TestContext.Current.CancellationToken);
            var control = await _start!.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.NotNull(control.Run);
            Assert.Equal(accepted.Run.RunId, control.Run.RunId);
            Assert.Equal(BackgroundChildState.Cancelling, control.Run.State);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, control.Run.OriginalContext));
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            Assert.Equal(accepted.Run.RunId, Assert.Single(before.OfType<ChildRunEvent.CancellationRequested>()).RunId);
            Assert.Equal(closureCommitted ? 1 : 0, before.OfType<ChildRunEvent.DispatchClosed>().Count());
            Assert.Empty(before.OfType<ChildRunEvent.TerminalRecorded>());
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            release.TrySetResult();
            await Journal.OnWrite.Pass();
            Assert.True(_childRelease.Task.IsCanceled);

            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber)
            { SessionId = Session, Filter = OutputFilter.Full }, recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = await OwnerAsync();
            Assert.NotEqual(owner, recovered);
            var rows = await ReadJournalPositionsAsync();
            var events = rows.Select(row => row.Event).ToArray();
            var terminalRow = Assert.Single(rows, row => row.Event is ChildRunEvent.TerminalRecorded);
            var terminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
            Assert.Equal(terminalRow.SequenceNr, terminal.TerminalSequenceNr);
            Assert.Equal(accepted.Run.RunId, terminal.RunId);
            Assert.False(terminal.Terminal.Lost);
            Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Terminal.Result.Completion);
            Assert.False(terminal.Terminal.Result.Success);
            Assert.Equal(SubAgentRunOutcome.Failed, terminal.Terminal.Result.Outcome);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
            Assert.Single(events.OfType<ChildRunEvent.CancellationRequested>());
            Assert.Single(events.OfType<ChildRunEvent.DispatchClosed>());
            var cancellationRow = Assert.Single(rows, row => row.Event is ChildRunEvent.CancellationRequested);
            var closureRow = Assert.Single(rows, row => row.Event is ChildRunEvent.DispatchClosed);
            Assert.True(cancellationRow.SequenceNr < closureRow.SequenceNr);
            Assert.True(closureRow.SequenceNr < terminalRow.SequenceNr);
            Assert.Single(events.OfType<ChildRunAccepted>());
            Assert.Single(events.OfType<ChildRunEvent.Started>());
            var retry = Assert.IsType<ChildStartReply.Accepted>(await recovered.Ask<ChildStartReply>(Retry(accepted),
                Ceiling, TestContext.Current.CancellationToken));
            Assert.Equal(BackgroundChildState.Cancelled, retry.State);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, retry.Run.OriginalContext));
            await AwaitAssertAsync(() =>
            {
                var delivered = _main.ReceivedMessages.SelectMany(messages => messages)
                    .SelectMany(message => message.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>())
                    .Where(result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(delivered);
                Assert.All(delivered, result =>
                {
                    using var body = JsonDocument.Parse(Assert.IsType<string>(result.Result));
                    Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
                    Assert.Equal("Cancelled", body.RootElement.GetProperty("state").GetString());
                    Assert.Equal(SubAgentOutcomeReason.CancelledByParent.Value, body.RootElement.GetProperty("reason").GetString());
                });
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, _child.CallCount);
            Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.DeliveryAdmitted>());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Daemon_restart_drain_cancels_a_live_child_before_acknowledgement_and_retains_its_terminal_snapshot()
    {
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        var child = await Sys.ActorSelection($"{owner.Path}/child-run-{accepted.Run.RunId.Value}")
            .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
        var ownerWatcher = CreateTestProbe();
        var childWatcher = CreateTestProbe();
        ownerWatcher.Watch(owner);
        childWatcher.Watch(child);
        var entered = NewSignal();
        var release = NewSignal();
        long terminalSequence = 0;
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ChildRunEvent.TerminalRecorded) return false;
            Interlocked.Exchange(ref terminalSequence, record.SequenceNr);
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        try
        {
            var drain = manager.Ask<DaemonRestartPrepared>(new PrepareForDaemonRestart(Session, "daemon-stop"),
                Ceiling, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            Assert.Equal(accepted.Run.RunId, Assert.Single(before.OfType<ChildRunEvent.CancellationRequested>()).RunId);
            Assert.Equal(accepted.Run.RunId, Assert.Single(before.OfType<ChildRunEvent.DispatchClosed>()).RunId);
            Assert.Empty(before.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.True(_childRelease.Task.IsCanceled);
            Assert.False(drain.IsCompleted);
            release.TrySetResult();
            var acknowledgement = await drain;
            Assert.Equal(Session, acknowledgement.SessionId);
            Assert.Null(acknowledgement.RestartReminder);
            await ownerWatcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            await childWatcher.ExpectTerminatedAsync(child, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            SessionSnapshot? snapshot = null;
            await AwaitAssertAsync(async () =>
            {
                snapshot = await ReadDrainedChildSnapshotAsync();
                Assert.NotNull(snapshot);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var stored = Assert.Single(snapshot!.ChildRuns);
            Assert.Equal(accepted.Run.RunId, stored.RunId);
            Assert.Equal(BackgroundChildState.Cancelled, stored.State);
            Assert.NotNull(stored.CancellationRequestedAtMs);
            Assert.NotNull(stored.DispatchClosedAtMs);
            Assert.Equal(Volatile.Read(ref terminalSequence), stored.TerminalSequenceNr);
            Assert.False(stored.Terminal!.Lost);
            Assert.False(stored.Terminal.Result.Success);
            Assert.Equal(SubAgentRunOutcome.Failed, stored.Terminal.Result.Outcome);
            Assert.IsType<ChildRunCompletion.Cancelled>(stored.Terminal.Result.Completion);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, stored.Terminal.Result.OutcomeReason);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, stored.OriginalContext));
            Assert.Equal(1, _child.CallCount);
        }
        finally { release.TrySetResult(); }
    }

    private async Task<SessionSnapshot?> ReadDrainedChildSnapshotAsync()
    {
        var done = new TaskCompletionSource<SessionSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new DrainedChildSnapshotReader($"session-{Session.Value}", done)));
        var watcher = CreateTestProbe();
        watcher.Watch(reader);
        try { return await done.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken); }
        finally
        {
            Sys.Stop(reader);
            await watcher.ExpectTerminatedAsync(reader, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private sealed class DrainedChildSnapshotReader : ReceivePersistentActor
    {
        public DrainedChildSnapshotReader(string persistenceId, TaskCompletionSource<SessionSnapshot?> done)
        {
            PersistenceId = persistenceId;
            SessionSnapshot? snapshot = null;
            Recover<SnapshotOffer>(offer =>
            {
                snapshot = (SessionSnapshot)offer.Snapshot;
                _ = SessionState.FromSnapshot(snapshot);
            });
            // This probe returns the stored snapshot. Later journal events do not alter that artifact.
            Recover<ISessionEvent>(_ => { });
            Recover<RecoveryCompleted>(_ => done.TrySetResult(snapshot));
        }
        public override string PersistenceId { get; }
    }
}
