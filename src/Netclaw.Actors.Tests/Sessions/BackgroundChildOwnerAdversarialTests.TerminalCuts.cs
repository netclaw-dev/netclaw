// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.TerminalCuts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;
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
    public async Task A_captured_terminal_requires_its_actual_child_sender_and_has_one_durable_winner()
    {
        var gate = InstallTerminalCutMailbox(nameof(BackgroundChildTerminal), child: false);
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await SendOriginalAsync(manager);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            _snapshots.InvocationStarted = NewSignal();
            _childRelease.TrySetResult();
            var captured = await gate.Held.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var message = Assert.IsType<BackgroundChildTerminal>(captured.Envelope.Message);
            Assert.Equal(owner, captured.Receiver);
            var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
            Assert.Equal(accepted.Run.RunId, message.RunId);
            Assert.NotEqual(owner, captured.Envelope.Sender);
            var foreign = CreateTestProbe();
            owner.Tell(message, foreign.Ref);
            owner.Tell(new Identify("after-foreign-terminal"), foreign.Ref);
            Assert.Equal(owner, (await foreign.ExpectMsgAsync<ActorIdentity>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken)).Subject);
            var rejected = await ReadJournalAsync();
            Assert.Empty(rejected.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Empty(rejected.OfType<ChildRunEvent.ResultPrepared>());
            Assert.Empty(rejected.OfType<ChildRunEvent.DeliveryAdmitted>());
            Assert.False(_snapshots.InvocationStarted.Task.IsCompleted);
            Assert.Equal(2, _main.CallCount);

            gate.Release();
            await CompletedAsync(subscriber);
            var committed = await ReadJournalPositionsAsync();
            var terminalRow = Assert.Single(committed, row => row.Event is ChildRunEvent.TerminalRecorded);
            var terminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
            Assert.Equal(terminalRow.SequenceNr, terminal.TerminalSequenceNr);
            Assert.Equal(message.Result.Output, terminal.Terminal.Result.Output);
            Assert.Equal(message.Result.Outcome, terminal.Terminal.Result.Outcome);
            Assert.Single(committed, row => row.Event is ChildRunEvent.ResultPrepared);
            Assert.Single(committed, row => row.Event is ChildRunEvent.DeliveryAdmitted);
            owner.Tell(message, captured.Envelope.Sender);
            await AssertTerminalCutOwnerBarrierAsync(owner, "after-duplicate-terminal");
            Assert.Equal(3, _main.CallCount);

            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber) { SessionId = Session, Filter = OutputFilter.Full }, recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var recovered = await OwnerAsync();
            Assert.NotEqual(owner, recovered);
            recovered.Tell(message, captured.Envelope.Sender);
            await AssertTerminalCutOwnerBarrierAsync(recovered, "after-old-generation-terminal");
            var final = await ReadJournalPositionsAsync();
            Assert.Equal(committed.Select(row => row.SequenceNr), final.Select(row => row.SequenceNr));
            Assert.Single(final, row => row.Event is ChildRunAccepted);
            Assert.Single(final, row => row.Event is ChildRunEvent.Started);
            Assert.Single(final, row => row.Event is ChildRunEvent.TerminalRecorded);
            Assert.Single(final, row => row.Event is ChildRunEvent.ResultPrepared);
            Assert.Single(final, row => row.Event is ChildRunEvent.DeliveryAdmitted);
            Assert.Equal(3, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task A_failed_terminal_write_cannot_acknowledge_enrich_or_admit_the_child_result()
    {
        var gate = InstallTerminalCutMailbox(nameof(BackgroundChildTerminalAck), child: true);
        var entered = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ChildRunEvent.TerminalRecorded) return false;
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
            _snapshots.InvocationStarted = NewSignal();
            await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
            {
                _childRelease.TrySetResult();
                await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                Assert.False(gate.Held.Task.IsCompleted);
                Assert.False(_snapshots.InvocationStarted.Task.IsCompleted);
                release.TrySetResult();
                await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);
            var final = await ReadJournalAsync();
            Assert.Single(final.OfType<ChildRunAccepted>());
            Assert.Single(final.OfType<ChildRunEvent.Started>());
            Assert.Empty(final.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Empty(final.OfType<ChildRunEvent.ResultPrepared>());
            Assert.Empty(final.OfType<ChildRunEvent.DeliveryAdmitted>());
            Assert.False(gate.Held.Task.IsCompleted);
            Assert.False(_snapshots.InvocationStarted.Task.IsCompleted);
            Assert.Equal(2, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
        }
        finally { release.TrySetResult(); gate.Release(); }
    }

    [Fact]
    public async Task A_real_enrichment_failure_preserves_the_committed_outcome_and_warns_in_its_delivery()
    {
        var (_, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        _snapshots.InvocationStarted = NewSignal();
        _snapshots.Failure = new InvalidOperationException("The neutral terminal snapshot failed.");
        _childRelease.TrySetResult();
        await _snapshots.InvocationStarted.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var final = await ReadJournalPositionsAsync();
        var terminalRow = Assert.Single(final, row => row.Event is ChildRunEvent.TerminalRecorded);
        var terminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
        var preparedRow = Assert.Single(final, row => row.Event is ChildRunEvent.ResultPrepared);
        var prepared = Assert.IsType<ChildRunEvent.ResultPrepared>(preparedRow.Event);
        var deliveryRow = Assert.Single(final, row => row.Event is ChildRunEvent.DeliveryAdmitted);
        var delivery = Assert.IsType<ChildRunEvent.DeliveryAdmitted>(deliveryRow.Event);
        Assert.True(terminal.Terminal.Result.Success);
        Assert.IsType<ChildRunCompletion.Completed>(terminal.Terminal.Result.Completion);
        Assert.Null(terminal.Terminal.EvidenceWarning);
        Assert.Equal(terminal.Terminal.Result.Output, prepared.Terminal.Result.Output);
        Assert.Equal(terminal.Terminal.Result.Outcome, prepared.Terminal.Result.Outcome);
        Assert.Equal(terminal.Terminal.Result.OutcomeReason, prepared.Terminal.Result.OutcomeReason);
        Assert.True(prepared.Terminal.Result.Success);
        Assert.Equal(terminal.TerminalSequenceNr, prepared.TerminalSequenceNr);
        Assert.True(terminalRow.SequenceNr < preparedRow.SequenceNr);
        Assert.True(preparedRow.SequenceNr < deliveryRow.SequenceNr);
        Assert.Equal("Result enrichment failed. The original terminal receipt remains unchanged.", prepared.Terminal.EvidenceWarning);
        using var body = JsonDocument.Parse(delivery.Input.UserMessage.Content!);
        Assert.Equal(prepared.Terminal.EvidenceWarning, body.RootElement.GetProperty("warning").GetString());
        Assert.Equal(terminal.Terminal.Result.Output, body.RootElement.GetProperty("output").GetString());
        var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
        var request = _main.ReceivedMessages[^1];
        Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()), call => call.CallId == id);
        Assert.Equal(delivery.Input.UserMessage.Content, Assert.IsType<string>(Assert.Single(
            request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), result => result.CallId == id).Result));
        Assert.Equal(3, _main.CallCount);
        Assert.Equal(1, _child.CallCount);
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("prepared")]
    [InlineData("admitted")]
    public async Task Cold_recovery_preserves_the_actual_terminal_prefix_without_child_or_delivery_replay(string cut)
    {
        var entered = NewSignal();
        var release = NewSignal();
        var intercepted = 0;
        ISessionEvent? pending = null;
        await Journal.OnWrite.FailIf(async record =>
        {
            var matches = cut switch
            {
                "terminal" => record.Payload is ChildRunEvent.TerminalRecorded,
                "prepared" => record.Payload is ChildRunEvent.ResultPrepared,
                _ => record.Payload is ChildRunEvent.DeliveryAdmitted
            };
            if (!matches || Interlocked.Exchange(ref intercepted, 1) != 0) return false;
            pending = Assert.IsAssignableFrom<ISessionEvent>(record.Payload);
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        var resume = NewSignal();
        try
        {
            await SendOriginalAsync(manager);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            _childRelease.TrySetResult();
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var prefix = await ReadJournalPositionsAsync();
            var acceptance = Assert.Single(prefix.Select(row => row.Event).OfType<ChildRunAccepted>());
            var terminal = cut == "terminal"
                ? Assert.IsType<ChildRunEvent.TerminalRecorded>(pending)
                : Assert.IsType<ChildRunEvent.TerminalRecorded>(Assert.Single(prefix,
                    row => row.Event is ChildRunEvent.TerminalRecorded).Event);
            Assert.Equal(cut == "terminal" ? 0 : 1, prefix.Count(row => row.Event is ChildRunEvent.TerminalRecorded));
            Assert.True(terminal.Terminal.Result.Success);
            Assert.Equal(cut == "admitted" ? 1 : 0, prefix.Count(row => row.Event is ChildRunEvent.ResultPrepared));
            Assert.DoesNotContain(prefix, row => row.Event is ChildRunEvent.DeliveryAdmitted);
            Assert.Equal(2, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
            var watcher = CreateTestProbe();
            watcher.Watch(owner);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            release.TrySetResult();
            await AwaitAssertAsync(async () =>
            {
                var stored = await ReadJournalAsync();
                Assert.Single(stored.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Equal(cut == "terminal" ? 0 : 1, stored.OfType<ChildRunEvent.ResultPrepared>().Count());
                Assert.Equal(cut == "admitted" ? 1 : 0, stored.OfType<ChildRunEvent.DeliveryAdmitted>().Count());
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            _main.NextResponseGate = resume;
            var recoveredSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(recoveredSubscriber) { SessionId = Session, Filter = OutputFilter.Full }, recoveredSubscriber.Ref);
            await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(owner, await OwnerAsync());
            await AwaitAssertAsync(() => Assert.Equal(3, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var resumed = await ReadJournalPositionsAsync();
            var actualTerminal = Assert.Single(resumed, row => row.Event is ChildRunEvent.TerminalRecorded);
            Assert.Equal(terminal.TerminalSequenceNr, actualTerminal.SequenceNr);
            var actual = Assert.IsType<ChildRunEvent.TerminalRecorded>(actualTerminal.Event);
            Assert.False(actual.Terminal.Lost);
            Assert.Equal(terminal.Terminal.Result.Output, actual.Terminal.Result.Output);
            Assert.Equal(terminal.Terminal.Result.Outcome, actual.Terminal.Result.Outcome);
            Assert.Single(resumed, row => row.Event is ChildRunEvent.ResultPrepared);
            var delivery = Assert.IsType<ChildRunEvent.DeliveryAdmitted>(Assert.Single(resumed,
                row => row.Event is ChildRunEvent.DeliveryAdmitted).Event);
            Assert.Equal(acceptance.Run.RunId, delivery.RunId);
            Assert.True(SessionState.SameCanonicalContext(acceptance.Run.OriginalContext, delivery.Input.TurnContext));
            var adoption = Assert.Single(resumed.Select(row => row.Event).OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            Assert.Equal(new[] { delivery.Input.InputId }, adoption.InputIds);
            Assert.True(SessionState.SameCanonicalContext(acceptance.Run.OriginalContext, adoption.TurnContext));
            var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
            var request = _main.ReceivedMessages[^1];
            var call = Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()), item => item.CallId == id);
            Assert.Equal(ChildRunDelivery.ToolName(acceptance.Run), call.Name);
            Assert.Equal(delivery.Input.UserMessage.Content, Assert.IsType<string>(Assert.Single(
                request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), item => item.CallId == id).Result));
            using var body = JsonDocument.Parse(delivery.Input.UserMessage.Content!);
            Assert.Equal(terminal.Terminal.Result.Output, body.RootElement.GetProperty("output").GetString());
            Assert.Equal(1, _child.CallCount);
            Assert.Empty(_start!.ProbeCallIds);
            resume.TrySetResult();
            await CompletedAsync(recoveredSubscriber);
            var final = await ReadJournalAsync();
            Assert.Single(final.OfType<ChildRunAccepted>());
            Assert.Single(final.OfType<ChildRunEvent.Started>());
            Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Single(final.OfType<ChildRunEvent.ResultPrepared>());
            Assert.Single(final.OfType<ChildRunEvent.DeliveryAdmitted>());
            Assert.Single(final.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            var consumed = final.OfType<TurnRecorded>().SelectMany(evt => evt.ConsumedInputIds)
                .Where(inputId => inputId == delivery.Input.InputId);
            Assert.Equal(new[] { delivery.Input.InputId }, consumed);
            Assert.Equal(3, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
        }
        finally { release.TrySetResult(); resume.TrySetResult(); }
    }

    private BackgroundChildApprovalGrantAdversarialTests.WaitReplyGate InstallTerminalCutMailbox(string messageName, bool child)
    {
        const string mailbox = "child-terminal-cut-mailbox";
        Sys.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            $"{mailbox} {{ mailbox-type = \"{typeof(BackgroundChildApprovalGrantAdversarialTests.WaitReplyMailbox).AssemblyQualifiedName}\" }}"));
        var gate = BackgroundChildApprovalGrantAdversarialTests.WaitReplyMailbox.Gates.GetOrCreateValue(Sys);
        gate.MessageName = messageName;
        gate.Arm();
        if (child) _start!.ChildMailbox = mailbox;
        else ((ExtendedActorSystem)Sys).Provider.Deployer.SetDeploy(new Deploy(
            $"/session-manager/{Uri.EscapeDataString(Session.Value)}", Config.Empty, NoRouter.Instance,
            LocalScope.Instance, Deploy.NoDispatcherGiven, mailbox));
        return gate;
    }

    private async Task AssertTerminalCutOwnerBarrierAsync(IActorRef owner, string label)
    {
        var barrier = CreateTestProbe();
        owner.Tell(new Identify(label), barrier.Ref);
        Assert.Equal(owner, (await barrier.ExpectMsgAsync<ActorIdentity>(Ceiling,
            cancellationToken: TestContext.Current.CancellationToken)).Subject);
    }
}
