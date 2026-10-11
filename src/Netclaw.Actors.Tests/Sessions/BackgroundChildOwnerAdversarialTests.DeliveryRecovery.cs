// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.DeliveryRecovery.cs" company="Petabridge, LLC">
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
    public async Task A_failed_coalesced_review_restores_every_durable_child_pair_after_cold_recovery()
    {
        _main.ToolCallsOnFirstCall!.Add(new FunctionCallContent("start-2", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Inspect the second part." }));
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = Session, Content = "Delegate both parts.", Source = DeliverySource("original", "operator-a") },
            Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await _secondChild.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var accepted = (await ReadJournalAsync()).OfType<ChildRunAccepted>().ToArray();
        Assert.Equal(2, accepted.Length);
        var parentRelease = NewSignal();
        var closeEntered = NewSignal();
        var closeRelease = NewSignal();
        var freshRelease = NewSignal();
        var failure = new InvalidOperationException("The actual coalesced review provider failed.");
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not InputClosed closed || closed.InputIds.Count != 2) return false;
            closeEntered.TrySetResult();
            await closeRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        _main.NextResponseGate = parentRelease;
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = Session, Content = "Inspect a separate task.", Source = DeliverySource("intervening", "operator-b") },
            Ceiling, TestContext.Current.CancellationToken);
        try
        {
            await AwaitAssertAsync(() => Assert.Equal(3, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            _childRelease.TrySetResult();
            _secondChildRelease.TrySetResult();
            ChildRunEvent.DeliveryAdmitted[] deliveries = [];
            await AwaitAssertAsync(async () =>
            {
                deliveries = (await ReadJournalAsync()).OfType<ChildRunEvent.DeliveryAdmitted>().ToArray();
                Assert.Equal(2, deliveries.Length);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var ids = deliveries.Select(delivery => delivery.Input.InputId).ToArray();
            Assert.Equal(2, ids.Distinct().Count());
            _main.PlannedExceptions.Enqueue(failure);
            await EventFilter.Error(contains: "turn_failed").ExpectOneAsync(async () =>
            {
                parentRelease.TrySetResult();
                await closeEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                var prefix = await ReadJournalAsync();
                var adoption = Assert.Single(prefix.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
                Assert.Equal(ids, adoption.InputIds);
                Assert.True(SessionState.SameCanonicalContext(accepted[0].Run.OriginalContext, adoption.TurnContext));
                Assert.Empty(prefix.OfType<InputClosed>());
                closeRelease.TrySetResult();
                var error = await subscriber.FishForMessageAsync<object>(message => message is ErrorOutput, Ceiling,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Same(failure, Assert.IsType<ErrorOutput>(error).Cause);
                Assert.Equal(TurnOutcome.Failed, Assert.IsType<TurnCompleted>(await CompletedAsync(subscriber)).Outcome);
            }, cancellationToken: TestContext.Current.CancellationToken);
            var beforeStop = await ReadJournalAsync();
            Assert.Equal(ids, Assert.Single(beforeStop.OfType<InputClosed>()).InputIds);
            Assert.Equal(2, beforeStop.OfType<TurnRecorded>().Count());
            Assert.Equal(3, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(1, _secondChild.CallCount);
            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            subscriber = CreateTestProbe();
            manager.Tell(new JoinSession(subscriber) { SessionId = Session, Filter = OutputFilter.Full }, subscriber.Ref);
            await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(owner, await OwnerAsync());
            Assert.Equal(3, _main.CallCount);
            _main.NextResponseGate = freshRelease;
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Inspect the recovered pairs.", Source = DeliverySource("fresh", "operator-b") },
                Ceiling, TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Equal(4, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var request = _main.ReceivedMessages[^1];
            var calls = request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
            var results = request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
            foreach (var delivery in deliveries)
            {
                var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
                var run = Assert.Single(accepted, admission => admission.Run.RunId == delivery.RunId).Run;
                Assert.Equal(ChildRunDelivery.ToolName(run), Assert.Single(calls, call => call.CallId == id).Name);
                Assert.Equal(delivery.Input.UserMessage.Content,
                    Assert.IsType<string>(Assert.Single(results, result => result.CallId == id).Result));
            }
            Assert.Equal(calls.Select(call => call.CallId).Order(), results.Select(result => result.CallId).Order());
            freshRelease.TrySetResult();
            await CompletedAsync(subscriber);
            var final = await ReadJournalAsync();
            Assert.Equal(2, final.OfType<ChildRunAccepted>().Count());
            Assert.Equal(2, final.OfType<ChildRunEvent.Started>().Count());
            Assert.Equal(2, final.OfType<ChildRunEvent.TerminalRecorded>().Count());
            Assert.Equal(2, final.OfType<ChildRunEvent.ResultPrepared>().Count());
            Assert.Equal(ids, final.OfType<ChildRunEvent.DeliveryAdmitted>().Select(delivery => delivery.Input.InputId));
            Assert.Single(final.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            Assert.Equal(4, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(1, _secondChild.CallCount);
        }
        finally
        {
            parentRelease.TrySetResult();
            closeRelease.TrySetResult();
            freshRelease.TrySetResult();
        }
    }
}
