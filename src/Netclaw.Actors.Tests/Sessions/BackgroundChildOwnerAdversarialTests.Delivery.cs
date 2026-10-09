// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.Delivery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Netclaw.Actors.SubAgents;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_terminals_keep_journal_order_and_original_evidence_after_reversed_enrichment(bool receiptFailure)
    {
        _main.ToolCallsOnFirstCall = null;
        static FunctionCallContent Probe(string id) => new(id, "neutral_probe", new Dictionary<string, object?>());
        _main.PlannedResponses.Enqueue([Probe("probe-1")]);
        _main.PlannedResponses.Enqueue([Probe("probe-2")]);
        var starts = new List<AIContent>
        {
            new FunctionCallContent("start-1", "start_probe", new Dictionary<string, object?> { ["_rationale"] = "Inspect the first part." }),
            new FunctionCallContent("start-2", "start_probe", new Dictionary<string, object?> { ["_rationale"] = "Inspect the second part." })
        };
        if (receiptFailure)
            starts.Add(new FunctionCallContent("defect-1", "missing_receipt_probe", new Dictionary<string, object?>()));
        _main.PlannedResponses.Enqueue(starts);
        if (!receiptFailure) _main.PlannedResponses.Enqueue([new TextContent("The original task has its two child runs.")]);
        _main.PlannedResponses.Enqueue([new TextContent("The fresh task has no tool effect.")]);
        _main.PlannedResponses.Enqueue([Probe("probe-3")]);
        _main.PlannedResponses.Enqueue([Probe("probe-4")]);
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = Session, Content = "Delegate the neutral task.", Source = DeliverySource("original", "operator-a") },
            Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await _secondChild.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var before = await ReadJournalAsync();
        var accepted = before.OfType<ChildRunAccepted>().ToArray();
        Assert.Equal(2, accepted.Length);
        Assert.Equal(2, _start!.ProbeCallIds.Count(id => id.StartsWith("probe-", StringComparison.Ordinal)));
        Assert.All(_start.ProbeRequesters, requester => Assert.Equal("operator-a", requester));
        var oldModelCount = _main.CallCount;
        Assert.Equal(receiptFailure ? 3 : 4, oldModelCount);
        if (receiptFailure)
            Assert.Equal(ToolCycleMessages.MissingReceipt, Assert.Single(before.OfType<TurnRecorded>()).AssistantReply.Content);
        var parentGate = NewSignal();
        var enrichmentGate = new TaskCompletionSource<WorkingContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.NextResponseGate = parentGate;
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Inspect the fresh task.", Source = DeliverySource("fresh", "operator-b")
        }, Ceiling, TestContext.Current.CancellationToken);
        try
        {
            await AwaitAssertAsync(() => Assert.Equal(oldModelCount + 1, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            _snapshots.InvocationStarted = NewSignal();
            _snapshots.Pending = enrichmentGate;
            _childRelease.TrySetResult();
            await _snapshots.InvocationStarted.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var firstPrefix = await ReadJournalPositionsAsync();
            var first = Assert.Single(firstPrefix, record => record.Event is ChildRunEvent.TerminalRecorded);
            var firstTerminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(first.Event);
            Assert.Equal(first.SequenceNr, firstTerminal.TerminalSequenceNr);
            _snapshots.Pending = null;
            _secondChildRelease.TrySetResult();
            (ISessionEvent Event, long SequenceNr)[] preparedSecond = [];
            await AwaitAssertAsync(async () =>
            {
                preparedSecond = await ReadJournalPositionsAsync();
                var prepared = Assert.Single(preparedSecond.Select(record => record.Event).OfType<ChildRunEvent.ResultPrepared>());
                Assert.NotEqual(firstTerminal.RunId, prepared.RunId);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            enrichmentGate.TrySetResult(new WorkingContextSnapshot
            { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() });
            (ISessionEvent Event, long SequenceNr)[] queued = [];
            await AwaitAssertAsync(async () =>
            {
                queued = await ReadJournalPositionsAsync();
                Assert.Equal(2, queued.Count(record => record.Event is ChildRunEvent.DeliveryAdmitted));
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var terminalRows = queued.Where(record => record.Event is ChildRunEvent.TerminalRecorded).ToArray();
            var terminals = terminalRows.Select(record => Assert.IsType<ChildRunEvent.TerminalRecorded>(record.Event)).ToArray();
            Assert.Equal(2, terminals.Length);
            Assert.Equal(terminals[0].RecordedAtMs, terminals[1].RecordedAtMs);
            Assert.True(terminals[0].TerminalSequenceNr < terminals[1].TerminalSequenceNr);
            Assert.All(terminalRows, record => Assert.Equal(record.SequenceNr,
                Assert.IsType<ChildRunEvent.TerminalRecorded>(record.Event).TerminalSequenceNr));
            var preparations = queued.Select(record => record.Event).OfType<ChildRunEvent.ResultPrepared>().ToArray();
            Assert.Equal(terminals.Select(terminal => terminal.RunId).Reverse(), preparations.Select(prepared => prepared.RunId));
            var deliveries = queued.Select(record => record.Event).OfType<ChildRunEvent.DeliveryAdmitted>().ToArray();
            Assert.Equal(terminals.Select(terminal => terminal.RunId), deliveries.Select(delivery => delivery.RunId));
            Assert.Equal(2, deliveries.Select(delivery => delivery.Input.UserMessage.ToolCallId).Distinct().Count());
            Assert.All(deliveries, delivery =>
            {
                Assert.Equal("original", delivery.Input.TurnContext.TurnId);
                Assert.Equal(new SenderId("operator-a"), delivery.Input.TurnContext.RequesterSenderId);
                Assert.DoesNotContain(delivery.Input.UserMessage.ToolCallId!.Value.Value, new[] { "start-1", "start-2" });
            });
            Assert.Equal(oldModelCount + 1, _main.CallCount);
            parentGate.TrySetResult();
            await CompletedAsync(subscriber);
            await CompletedAsync(subscriber);
            var after = await ReadJournalAsync();
            var childAdoption = Assert.Single(after.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            Assert.Equal(deliveries.Select(delivery => delivery.Input.InputId), childAdoption.InputIds);
            Assert.Equal("original", childAdoption.TurnContext.TurnId);
            Assert.Equal(new SenderId("operator-a"), childAdoption.TurnContext.RequesterSenderId);
            var freshAdoption = Assert.Single(after.OfType<ToolTaskAdopted>(), evt => evt.TurnContext.TurnId == "fresh");
            Assert.Equal(new SenderId("operator-b"), freshAdoption.TurnContext.RequesterSenderId);
            var final = after.OfType<TurnRecorded>().Last();
            var deliveryIds = deliveries.Select(delivery => delivery.Input.InputId).ToArray();
            var consumed = after.SelectMany(evt => evt switch
            {
                ToolBatchStarted batch => batch.ConsumedInputIds,
                TurnRecorded turn => turn.ConsumedInputIds,
                InputClosed closed => closed.InputIds,
                _ => Array.Empty<InputId>()
            }).Where(deliveryIds.Contains);
            Assert.Equal(deliveryIds, consumed);
            Assert.Equal(receiptFailure ? ToolCycleMessages.MissingReceipt : ToolCycleMessages.Final, final.AssistantReply.Content);
            Assert.Equal(oldModelCount + (receiptFailure ? 1 : 3), _main.CallCount);
            Assert.Equal(new[] { "probe-1", "probe-2" }, _start.ProbeCallIds.Where(id => id.StartsWith("probe-", StringComparison.Ordinal)));
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(1, _secondChild.CallCount);
            Assert.Equal(2, after.OfType<ChildRunAccepted>().Count());
            Assert.Equal(2, after.OfType<ChildRunEvent.Started>().Count());
            Assert.Equal(2, after.OfType<ChildRunEvent.TerminalRecorded>().Count());
            Assert.Equal(2, after.OfType<ChildRunEvent.ResultPrepared>().Count());
            Assert.Equal(2, after.OfType<ChildRunEvent.DeliveryAdmitted>().Count());
            if (!receiptFailure)
            {
                var request = _main.ReceivedMessages[oldModelCount + 1];
                var calls = request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
                var results = request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
                foreach (var delivery in deliveries)
                {
                    var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
                    Assert.Single(calls, call => call.CallId == id);
                    Assert.Equal(delivery.Input.UserMessage.Content,
                        Assert.IsType<string>(Assert.Single(results, result => result.CallId == id).Result));
                }
                var correction = Assert.Single(after.OfType<ToolCallRecorded>(), evt => evt.ToolResult.ToolCallId == new ToolCallId("probe-3"));
                Assert.True(correction.LoopObservation!.Synthetic);
                Assert.StartsWith(ToolCycleMessages.Correction, correction.ToolResult.Content);
            }
        }
        finally
        {
            _snapshots.Pending = null;
            enrichmentGate.TrySetResult(new WorkingContextSnapshot
            { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() });
            parentGate.TrySetResult();
        }
    }

    [Fact]
    public async Task A_failed_first_child_review_retains_one_canonical_pair_after_closed_input_and_cold_recovery()
    {
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var original = await ReadJournalAsync();
        var accepted = Assert.Single(original.OfType<ChildRunAccepted>());
        var deliveryInputId = new InputId($"child-result-{accepted.Run.RunId.Value}");
        var closeEntered = NewSignal();
        var closeRelease = NewSignal();
        var freshRelease = NewSignal();
        var failure = new InvalidOperationException("The first child review failed at the actual provider boundary.");
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not InputClosed closed || !closed.InputIds.Contains(deliveryInputId)) return false;
            closeEntered.TrySetResult();
            await closeRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        _main.PlannedExceptions.Enqueue(failure);
        try
        {
            await EventFilter.Error(contains: "turn_failed").ExpectOneAsync(async () =>
            {
                _childRelease.TrySetResult();
                await closeEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                var prefix = await ReadJournalAsync();
                Assert.Single(prefix.OfType<ChildRunEvent.DeliveryAdmitted>());
                Assert.Single(prefix.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId == accepted.Run.RunId);
                Assert.Empty(prefix.OfType<InputClosed>());
                closeRelease.TrySetResult();
                var error = await subscriber.FishForMessageAsync<object>(message => message is ErrorOutput, Ceiling,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Same(failure, Assert.IsType<ErrorOutput>(error).Cause);
                var completed = await CompletedAsync(subscriber);
                Assert.Equal(TurnOutcome.Failed, Assert.IsType<TurnCompleted>(completed).Outcome);
            }, cancellationToken: TestContext.Current.CancellationToken);
            var closedPrefix = await ReadJournalAsync();
            var delivery = Assert.Single(closedPrefix.OfType<ChildRunEvent.DeliveryAdmitted>());
            var closure = Assert.Single(closedPrefix.OfType<InputClosed>());
            Assert.Equal(new[] { deliveryInputId }, closure.InputIds);
            Assert.Equal("original", closure.TaskId);
            Assert.Single(closedPrefix.OfType<TurnRecorded>());
            Assert.Single(closedPrefix.OfType<ToolBatchStarted>());
            Assert.Equal(2, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            subscriber = CreateTestProbe();
            manager.Tell(new JoinSession(subscriber) { SessionId = Session, Filter = OutputFilter.Full }, subscriber.Ref);
            await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var recoveredOwner = await OwnerAsync();
            Assert.NotEqual(owner, recoveredOwner);
            Assert.Equal(2, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            _main.NextResponseGate = freshRelease;
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Inspect the recovered record.", Source = DeliverySource("fresh", "operator-b") },
                Ceiling, TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Equal(3, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var request = _main.ReceivedMessages[^1];
            var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
            var calls = request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
            var results = request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
            var call = Assert.Single(calls, candidate => candidate.CallId == id);
            Assert.Equal(ChildRunDelivery.ToolName(accepted.Run), call.Name);
            Assert.Equal(delivery.Input.UserMessage.Content,
                Assert.IsType<string>(Assert.Single(results, candidate => candidate.CallId == id).Result));
            Assert.Equal(calls.Select(candidate => candidate.CallId).Order(), results.Select(candidate => candidate.CallId).Order());
            freshRelease.TrySetResult();
            await CompletedAsync(subscriber);
            var final = await ReadJournalAsync();
            Assert.Single(final.OfType<ChildRunAccepted>());
            Assert.Single(final.OfType<ChildRunEvent.Started>());
            Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Single(final.OfType<ChildRunEvent.ResultPrepared>());
            Assert.Single(final.OfType<ChildRunEvent.DeliveryAdmitted>());
            var childAdoption = Assert.Single(final.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, childAdoption.TurnContext));
            Assert.DoesNotContain(final.OfType<ToolCallRecorded>(), evt => evt.ToolResult.ToolCallId == delivery.Input.UserMessage.ToolCallId);
            Assert.Equal(3, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(0, _secondChild.CallCount);
        }
        finally
        {
            closeRelease.TrySetResult();
            freshRelease.TrySetResult();
        }
    }

    private static MessageSource DeliverySource(string turn, string requester) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester), TurnId = new TurnId(turn), MessageId = turn,
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", requester, requester)
    };
}
