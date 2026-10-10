// -----------------------------------------------------------------------
// <copyright file="BackgroundChildLedgerAdversarialTests.TerminalPositions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using StoredRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildLedgerAdversarialTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_recovery_checks_terminal_positions_before_any_child_result_continuation(bool snapshot, bool invalid)
    {
        var (premise, prototype) = Acceptance(Owner, slash: true);
        var input = premise.PendingInputs[0];
        var adoption = new ToolTaskAdopted(false)
        { SessionId = Owner, TurnContext = input.TurnContext, InputIds = [input.InputId] };
        var state = SessionState.Empty.Apply(input).Apply(adoption);
        var run = prototype with
        { OriginInputIds = [input.InputId], ParentCheckpoint = state.LoopCheckpoint, ParentReceiptFailure = false };
        var accepted = new ChildRunAccepted { SessionId = Owner, Run = run };
        var started = new ChildRunEvent.Started { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123457 };
        var turn = new TurnRecorded
        {
            SessionId = Owner, UserMessage = input.UserMessage, ConsumedInputIds = [input.InputId], RecordedAtMs = 123458,
            AssistantReply = new SerializableChatMessage { Role = StoredRole.Assistant, Content = "The original task completed." }
        };
        var terminal = Terminal(run);
        // A positive but incorrect stamp remains self-consistent until the actor checks the real store position.
        var terminalSequence = !snapshot && invalid ? 7L : 6L;
        var recorded = new ChildRunEvent.TerminalRecorded(terminal, terminalSequence)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123459 };
        var prepared = new ChildRunEvent.ResultPrepared(terminal, terminalSequence)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123459 };
        var title = new SessionTitleSet { SessionId = Owner, Title = "A durable position marker", SetAtMs = 123460 };
        var events = new ISessionEvent[] { input, adoption, accepted, started, turn, recorded, prepared, title };
        var seed = Sys.ActorOf(Props.Create(() => new SnapshotSeeder($"session-{Owner.Value}")));
        for (var index = 0; index < events.Length; index++)
        {
            var serializer = Codec(events[index]);
            var decoded = Assert.IsAssignableFrom<ISessionEvent>(Sys.Serialization.Deserialize(
                serializer.ToBinary(events[index]), serializer.Identifier, serializer.Manifest(events[index])));
            Assert.Equal(index + 1L, await seed.Ask<long>(decoded, Ceiling, TestContext.Current.CancellationToken));
        }
        state = state.Apply(accepted).Apply(started).Apply(turn).CloseInputs([input.InputId])
            .Apply(recorded).Apply(prepared).Apply(title);
        var storedRun = state.ChildRuns[run.RunId];
        Assert.Equal(terminalSequence, storedRun.TerminalSequenceNr);
        if (snapshot)
        {
            if (invalid)
                state = state with { ChildRuns = state.ChildRuns.SetItem(run.RunId, storedRun with { TerminalSequenceNr = 9 }) };
            var stored = RoundTrip(state.ToSnapshot());
            Assert.Single(SessionState.FromSnapshot(stored).ChildRuns);
            long savedAt = 0;
            await Snapshots.OnSave.FailIf((id, criteria) =>
            {
                if (id == $"session-{Owner.Value}") savedAt = criteria.MaxSequenceNr;
                return false;
            });
            await seed.Ask<Done>(stored, Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(8, savedAt);
            Assert.Equal(invalid, Assert.Single(stored.ChildRuns).TerminalSequenceNr > savedAt);
            if (!invalid) Assert.True(storedRun.TerminalSequenceNr < savedAt);
        }
        var watcher = CreateTestProbe();
        watcher.Watch(seed);
        Sys.Stop(seed);
        await watcher.ExpectTerminatedAsync(seed, Ceiling, cancellationToken: TestContext.Current.CancellationToken);

        var entered = NewSignal();
        var release = NewSignal();
        var response = NewSignal();
        _model.NextResponseGate = response;
        await Snapshots.OnLoad.FailIf(async (id, _) =>
        {
            if (id != $"session-{Owner.Value}") return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var observer = CreateTestProbe();
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        async Task RecoverAsync()
        {
            manager.Tell(new JoinSession(observer) { SessionId = Owner, Filter = OutputFilter.Full | OutputFilter.ProcessingState }, observer.Ref);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(Owner.Value)}")
                .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
            observer.Watch(owner);
            release.TrySetResult();
            var first = await observer.ReceiveOneAsync(Ceiling, TestContext.Current.CancellationToken);
            if (invalid)
            {
                Assert.Equal(owner, Assert.IsType<Terminated>(first).ActorRef);
                return;
            }
            Assert.Equal(Owner, Assert.IsType<SessionJoined>(first).SessionId);
            await _model.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            response.TrySetResult();
            var completed = false;
            await observer.FishForMessageAsync<object>(message =>
            {
                if (message is TurnCompleted) completed = true;
                return completed && message is ProcessingStateOutput { IsProcessing: false };
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        try
        {
            if (invalid)
                await EventFilter.Error().ExpectOneAsync(RecoverAsync, cancellationToken: TestContext.Current.CancellationToken);
            else
                await RecoverAsync();
        }
        finally
        {
            release.TrySetResult();
            response.TrySetResult();
        }
        Assert.Equal(invalid ? 0 : 1, _model.CallCount);
        Assert.Equal(0, _tools.CallCount);
        if (!invalid)
        {
            var request = Assert.Single(_model.ReceivedMessages);
            var call = Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()));
            var result = Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal(ChildRunDelivery.ToolName(storedRun), call.Name);
            Assert.StartsWith("child-result-", call.CallId);
            Assert.Equal(call.CallId, result.CallId);
            Assert.Equal(ChildRunDelivery.Body(storedRun), Assert.IsType<string>(result.Result));
            Assert.Contains(request, message => message.Text == turn.AssistantReply.Content);
        }
    }
}
