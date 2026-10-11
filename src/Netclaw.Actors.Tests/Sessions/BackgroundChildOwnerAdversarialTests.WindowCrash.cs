// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.WindowCrash.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Fact]
    public async Task A_failed_child_adoption_write_cannot_dispatch_before_recovery_commits_the_new_window()
    {
        _main.ToolCallsOnFirstCall = null;
        _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-probe-1")]);
        _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-probe-2")]);
        _main.PlannedResponses.Enqueue([new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the assigned task." })]);
        _main.PlannedResponses.Enqueue([new TextContent("The child owns its accepted task.")]);
        var entered = NewSignal();
        var release = NewSignal();
        var recoveredResponse = NewSignal();
        var adoptionWrites = 0;
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ToolTaskAdopted { ContinuedChildRunId: not null }
                || Interlocked.Increment(ref adoptionWrites) != 1)
                return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        });
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await SendOriginalAsync(manager);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
            var retained = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(accepted), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, retained.Run.OriginalContext));
            await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
            {
                _childRelease.TrySetResult();
                await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                var held = await ReadJournalAsync();
                Assert.Single(held.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Single(held.OfType<ChildRunEvent.ResultPrepared>());
                Assert.Single(held.OfType<ChildRunEvent.DeliveryAdmitted>());
                Assert.DoesNotContain(held.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
                Assert.Equal(4, _main.CallCount);
                Assert.Equal(new[] { "prefix-probe-1", "prefix-probe-2" }, _start!.ProbeCallIds);
                release.TrySetResult();
                await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("recovered-fresh-window")]);
            _main.PlannedResponses.Enqueue([new TextContent("The recovered parent completed its authorized probe.")]);
            _main.NextResponseGate = recoveredResponse;
            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber) { SessionId = Session, Filter = OutputFilter.Full }, recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(owner, await OwnerAsync());
            await AwaitAssertAsync(() => Assert.Equal(5, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            recoveredResponse.TrySetResult();
            await CompletedAsync(recoveredSubscriber);
            var final = await ReadJournalPositionsAsync();
            Assert.Single(final, row => row.Event is ChildRunAccepted);
            Assert.Single(final, row => row.Event is ChildRunEvent.TerminalRecorded);
            Assert.Single(final, row => row.Event is ChildRunEvent.ResultPrepared);
            var delivery = Assert.IsType<ChildRunEvent.DeliveryAdmitted>(Assert.Single(final,
                row => row.Event is ChildRunEvent.DeliveryAdmitted).Event);
            var adoption = Assert.Single(final, row => row.Event is ToolTaskAdopted { ContinuedChildRunId: not null });
            var committed = Assert.IsType<ToolTaskAdopted>(adoption.Event);
            Assert.True(committed.StartsChildContinuationWindow);
            Assert.Equal(accepted.Run.RunId, committed.ContinuedChildRunId);
            Assert.Equal(new[] { delivery.Input.InputId }, committed.InputIds);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, committed.TurnContext));
            var observation = Assert.Single(final, row => row.Event is ToolCallRecorded call
                && call.ToolResult.ToolCallId == new ToolCallId("recovered-fresh-window"));
            Assert.True(adoption.SequenceNr < observation.SequenceNr);
            Assert.False(Assert.IsType<ToolCallRecorded>(observation.Event).LoopObservation!.Synthetic);
            Assert.Equal(new[] { "prefix-probe-1", "prefix-probe-2", "recovered-fresh-window" }, _start!.ProbeCallIds);
            Assert.Equal(2, adoptionWrites);
            Assert.Equal(6, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
        }
        finally
        {
            release.TrySetResult();
            _childRelease.TrySetResult();
            recoveredResponse.TrySetResult();
        }
    }
}
