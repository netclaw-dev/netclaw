// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.AdmittedRecovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Fact]
    public async Task A_trusted_restart_after_child_input_consumption_preserves_post_adoption_detector_evidence()
    {
        PrepareAdmittedRecoveryRounds();
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Delegate the assigned task.",
            Source = DeliverySource("original", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var oldResponse = NewSignal();
        var recoveredResponse = NewSignal();
        _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-probe-1")]);
        _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-probe-2")]);
        await Journal.OnWrite.FailIf(record =>
        {
            if (record.Payload is ToolCallRecorded { ToolResult.ToolCallId: { Value: "prefix-probe-2" } })
                _main.NextResponseGate = oldResponse;
            return false;
        });
        try
        {
            _childRelease.TrySetResult();
            await AwaitAssertAsync(() => Assert.Equal(5, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var prefix = await ReadJournalPositionsAsync();
            var accepted = Assert.Single(prefix.Select(row => row.Event).OfType<ChildRunAccepted>());
            var delivery = Assert.Single(prefix.Select(row => row.Event).OfType<ChildRunEvent.DeliveryAdmitted>());
            var adopted = Assert.Single(prefix.Select(row => row.Event).OfType<ToolTaskAdopted>(),
                evt => evt.ContinuedChildRunId == accepted.Run.RunId);
            Assert.Equal(new[] { delivery.Input.InputId }, adopted.InputIds);
            Assert.True(adopted.StartsChildContinuationWindow);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, adopted.TurnContext));
            Assert.Empty(prefix.Select(row => row.Event).OfType<InputClosed>());
            Assert.Single(prefix.Select(row => row.Event).OfType<TurnRecorded>());
            AssertAdmittedRecoveryPair(_main.ReceivedMessages[^1], delivery, accepted.Run);
            Assert.Equal(new[] { "prefix-probe-1", "prefix-probe-2" }, _start!.ProbeCallIds);
            var postAdoption = prefix.Where(row => row.Event is ToolCallRecorded call
                && call.ToolResult.ToolCallId is { } id && id.Value.StartsWith("prefix-probe-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, postAdoption.Length);
            var adoptionPosition = Assert.Single(prefix, row => ReferenceEquals(row.Event, adopted)).SequenceNr;
            Assert.All(postAdoption, row => Assert.True(row.SequenceNr > adoptionPosition));
            Assert.All(postAdoption, row => Assert.False(Assert.IsType<ToolCallRecorded>(row.Event).LoopObservation!.Synthetic));
            var confirmed = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(accepted), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(2, Assert.Single(confirmed.Run.ParentCheckpoint.Entries,
                entry => entry.ToolName == "neutral_probe").EqualRounds);

            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            _main.NextResponseGate = recoveredResponse;
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-refused-3")]);
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-stopped-4")]);
            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber) { SessionId = Session, Filter = OutputFilter.Full },
                recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = await OwnerAsync();
            Assert.NotEqual(owner, recovered);
            await AssertTerminalCutOwnerBarrierAsync(recovered, "consumed-child-before-trusted-resume");
            Assert.Equal(5, _main.CallCount);
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = Session, Content = "Resume the work that was interrupted by the daemon restart.",
                Source = DeliverySource("restart-resume-consumed-child:1", "reminder-system") with
                {
                    ReminderId = new ReminderId("restart-resume-consumed-child:1"),
                    Principal = PrincipalClassification.VerifiedAutomation
                }
            }, Ceiling, TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Equal(6, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var resumed = await ReadJournalPositionsAsync();
            Assert.Equal(prefix.Select(row => row.SequenceNr), resumed.Select(row => row.SequenceNr));
            AssertAdmittedRecoveryPair(_main.ReceivedMessages[^1], delivery, accepted.Run);
            Assert.Single(resumed.Select(row => row.Event).OfType<ToolTaskAdopted>(),
                evt => evt.ContinuedChildRunId == accepted.Run.RunId);
            var restored = Assert.IsType<ChildStartReply.Accepted>(await recovered.Ask<ChildStartReply>(Retry(accepted), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.True(BackgroundChildRun.SameCheckpoint(confirmed.Run.ParentCheckpoint, restored.Run.ParentCheckpoint));
            Assert.Equal(1, _child.CallCount);
            recoveredResponse.TrySetResult();
            await CompletedAsync(recoveredSubscriber);
            var final = await ReadJournalPositionsAsync();
            AssertAdmittedRecoverySettlement(final, delivery, accepted.Run, ["prefix-probe-1", "prefix-probe-2"]);
            Assert.Equal(7, _main.CallCount);
        }
        finally
        {
            oldResponse.TrySetResult();
            recoveredResponse.TrySetResult();
        }
    }

    [Fact]
    public async Task Cold_recovery_keeps_an_ordinary_head_before_its_pending_child_result()
    {
        PrepareAdmittedRecoveryRounds();
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Delegate the assigned task.",
            Source = DeliverySource("original", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var oldResponse = NewSignal();
        var recoveredResponse = NewSignal();
        _main.NextResponseGate = oldResponse;
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = Session, Content = "Complete the ordinary head first.",
                Source = DeliverySource("ordinary-head", "operator-b")
            }, Ceiling, TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Equal(3, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            _childRelease.TrySetResult();
            ChildRunEvent.DeliveryAdmitted? delivery = null;
            await AwaitAssertAsync(async () =>
            {
                delivery = Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.DeliveryAdmitted>());
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var prefix = await ReadJournalPositionsAsync();
            var accepted = Assert.Single(prefix.Select(row => row.Event).OfType<ChildRunAccepted>());
            var ordinary = Assert.Single(prefix.Select(row => row.Event).OfType<InputAdmitted>(),
                input => input.TurnContext.TurnId == "ordinary-head");
            var ordinaryAdoption = Assert.Single(prefix.Select(row => row.Event).OfType<ToolTaskAdopted>(),
                evt => evt.TurnContext.TurnId == "ordinary-head");
            Assert.True(SessionState.SameCanonicalContext(ordinary.TurnContext, ordinaryAdoption.TurnContext));
            Assert.Equal(new SenderId("operator-b"), ordinaryAdoption.TurnContext.RequesterSenderId);
            Assert.DoesNotContain(prefix, row => row.Event is ToolTaskAdopted { ContinuedChildRunId: not null });
            Assert.True(Assert.Single(prefix, row => row.Event is InputAdmitted input
                && input.InputId == ordinary.InputId).SequenceNr < Assert.Single(prefix,
                row => row.Event is ChildRunEvent.DeliveryAdmitted).SequenceNr);
            Assert.Single(prefix.Select(row => row.Event).OfType<TurnRecorded>());

            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber) { SessionId = Session, Filter = OutputFilter.Full },
                recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = await OwnerAsync();
            Assert.NotEqual(owner, recovered);
            await AssertTerminalCutOwnerBarrierAsync(recovered, "ordinary-prefix-before-resume");
            Assert.Equal(3, _main.CallCount);
            var waiting = await ReadJournalPositionsAsync();
            Assert.Equal(prefix.Select(row => row.SequenceNr), waiting.Select(row => row.SequenceNr));

            _main.NextResponseGate = recoveredResponse;
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("ordinary-probe")]);
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-probe-1")]);
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-probe-2")]);
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-refused-3")]);
            _main.PlannedResponses.Enqueue([AdmittedRecoveryProbe("prefix-stopped-4")]);
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = Session, Content = "Resume the work that was interrupted by the daemon restart.",
                Source = DeliverySource("restart-resume-child-prefix:1", "reminder-system") with
                {
                    ReminderId = new ReminderId("restart-resume-child-prefix:1"),
                    Principal = PrincipalClassification.VerifiedAutomation
                }
            }, Ceiling, TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Equal(4, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var ordinaryRequest = _main.ReceivedMessages[^1];
            Assert.Contains(ordinaryRequest, message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text == ordinary.UserMessage.Content);
            var deliveryId = delivery!.Input.UserMessage.ToolCallId!.Value.Value;
            Assert.DoesNotContain(ordinaryRequest.SelectMany(message => message.Contents.OfType<FunctionCallContent>()),
                call => call.CallId == deliveryId);
            Assert.DoesNotContain(ordinaryRequest.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
                result => result.CallId == deliveryId);
            Assert.DoesNotContain(await ReadJournalAsync(), evt => evt is ToolTaskAdopted { ContinuedChildRunId: not null });
            recoveredResponse.TrySetResult();
            await CompletedAsync(recoveredSubscriber);
            var final = await ReadJournalPositionsAsync();
            AssertAdmittedRecoverySettlement(final, delivery, accepted.Run,
                ["ordinary-probe", "prefix-probe-1", "prefix-probe-2"]);
            Assert.Equal(new[] { "operator-b", "operator-a", "operator-a" }, _start!.ProbeRequesters);
            Assert.Single(final.Select(row => row.Event).OfType<ToolTaskAdopted>(),
                evt => evt.TurnContext.TurnId == "ordinary-head");
            var childAdoption = Assert.Single(final,
                row => row.Event is ToolTaskAdopted { ContinuedChildRunId: not null });
            var ordinaryBatch = Assert.Single(final, row => row.Event is ToolBatchStarted batch
                && batch.ConsumedInputIds.Contains(ordinary.InputId));
            Assert.True(ordinaryBatch.SequenceNr < childAdoption.SequenceNr);
            Assert.Equal(new[] { ordinary.InputId, delivery.Input.InputId }, final.SelectMany(row => row.Event switch
            {
                ToolBatchStarted batch => batch.ConsumedInputIds,
                TurnRecorded turn => turn.ConsumedInputIds,
                InputClosed closed => closed.InputIds,
                _ => Array.Empty<InputId>()
            }).Where(id => id == ordinary.InputId || id == delivery.Input.InputId));
            AssertAdmittedRecoveryPair(_main.ReceivedMessages[6], delivery, accepted.Run);
            Assert.Equal(8, _main.CallCount);
        }
        finally
        {
            oldResponse.TrySetResult();
            recoveredResponse.TrySetResult();
        }
    }

    private void PrepareAdmittedRecoveryRounds()
    {
        _main.ToolCallsOnFirstCall = null;
        _main.PlannedResponses.Enqueue([new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Inspect the neutral fixture." })]);
        _main.PlannedResponses.Enqueue([new TextContent("The original child run was accepted.")]);
    }

    private static FunctionCallContent AdmittedRecoveryProbe(string id)
        => new(id, "neutral_probe", new Dictionary<string, object?>());

    private static void AssertAdmittedRecoveryPair(IReadOnlyList<ChatMessage> request,
        ChildRunEvent.DeliveryAdmitted delivery, BackgroundChildRun run)
    {
        var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
        var call = Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()),
            item => item.CallId == id);
        Assert.Equal(ChildRunDelivery.ToolName(run), call.Name);
        Assert.Equal(delivery.Input.UserMessage.Content, Assert.IsType<string>(Assert.Single(
            request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            item => item.CallId == id).Result));
        Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            item => item.CallId == "start-1");
    }

    private void AssertAdmittedRecoverySettlement((ISessionEvent Event, long SequenceNr)[] rows,
        ChildRunEvent.DeliveryAdmitted delivery, BackgroundChildRun run, string[] executed)
    {
        var events = rows.Select(row => row.Event).ToArray();
        Assert.Single(events.OfType<ChildRunAccepted>());
        Assert.Single(events.OfType<ChildRunEvent.Started>());
        Assert.Single(events.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.Single(events.OfType<ChildRunEvent.ResultPrepared>());
        Assert.Single(events.OfType<ChildRunEvent.DeliveryAdmitted>());
        var adoption = Assert.Single(events.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId == run.RunId);
        Assert.Equal(new[] { delivery.Input.InputId }, adoption.InputIds);
        Assert.True(SessionState.SameCanonicalContext(run.OriginalContext, adoption.TurnContext));
        Assert.Equal(new SenderId("operator-a"), adoption.TurnContext.RequesterSenderId);
        Assert.Equal(executed, _start!.ProbeCallIds);
        var refused = Assert.Single(events.OfType<ToolCallRecorded>(),
            evt => evt.ToolResult.ToolCallId == new ToolCallId("prefix-refused-3"));
        Assert.True(Assert.IsType<ToolLoopObservation>(refused.LoopObservation).Synthetic);
        Assert.StartsWith(ToolCycleMessages.Correction, refused.ToolResult.Content);
        Assert.DoesNotContain(events.OfType<ToolBatchStarted>(),
            evt => evt.AssistantMessage.ToolCalls.Any(call => call.CallId == new ToolCallId("prefix-stopped-4")));
        var refusedBatch = Assert.Single(events.OfType<ToolBatchStarted>(),
            evt => evt.AssistantMessage.ToolCalls.Any(call => call.CallId == new ToolCallId("prefix-refused-3")));
        Assert.Equal(run.ParentCheckpoint.TaskId, refusedBatch.LoopAdmission!.TaskId);
        Assert.Contains("prefix-refused-3", refusedBatch.LoopAdmission.RefusedCallIds);
        Assert.Equal(new[] { delivery.Input.InputId }, events.SelectMany(evt => evt switch
        {
            ToolBatchStarted batch => batch.ConsumedInputIds,
            TurnRecorded turn => turn.ConsumedInputIds,
            InputClosed closed => closed.InputIds,
            _ => Array.Empty<InputId>()
        }).Where(id => id == delivery.Input.InputId));
        var settlement = Assert.Single(events.OfType<TurnRecorded>(),
            evt => evt.AssistantReply.Content == ToolCycleMessages.Final);
        Assert.Equal(ToolCycleMessages.Final, settlement.AssistantReply.Content);
        Assert.Empty(events.OfType<InputClosed>());
        Assert.Equal(1, _child.CallCount);
    }
}
