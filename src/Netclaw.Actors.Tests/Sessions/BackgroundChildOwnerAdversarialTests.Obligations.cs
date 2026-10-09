// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.Obligations.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Fact]
    public async Task A_child_result_waits_for_actual_compaction_and_restores_its_original_authority()
    {
        _main.ToolCallsOnFirstCall = null;
        var (owner, manager, subscriber) = await CreateObligationOwnerAsync();
        for (var seed = 0; seed < 3; seed++)
        {
            await SendObligationInputAsync(manager, $"seed-{seed}", "operator-a");
            await CompletedObligationTurnAsync(subscriber);
        }
        _main.PlannedResponses.Enqueue([new FunctionCallContent("start-1", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the original task." })]);
        await SendObligationInputAsync(manager, "original", "operator-a");
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedObligationTurnAsync(subscriber);
        var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        Assert.Equal(5, _main.CallCount);
        var observerRelease = NewSignal();
        var reviewRelease = NewSignal();
        var snapshotRelease = NewSignal();
        Assert.Equal(1, _child.CallCount);
        Assert.Single(_child.ReceivedMessages);
        Assert.False(_childRelease.Task.IsCompleted);
        await AwaitAssertAsync(() => Assert.Null(_child.NextResponseGate), Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        _child.NextResponseGate = observerRelease;
        _child.ObservationResponseOverride = "The prior neutral turns completed. The original child remains independent.";
        static bool IsSummarizer(IReadOnlyList<ChatMessage> request)
            => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.System
                && message.Text?.Contains("You are a session summarizer", StringComparison.Ordinal) == true);
        await Snapshots.OnSave.FailIf(async (persistenceId, _) =>
        {
            if (persistenceId == $"session-{Session.Value}")
                await snapshotRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        try
        {
            _main.PlannedUsageOverrides.Enqueue(new UsageDetails { InputTokenCount = 100000, OutputTokenCount = 1 });
            await SendObligationInputAsync(manager, "foreign-before-compaction", "operator-b");
            await CompletedAsync(subscriber);
            await AwaitAssertAsync(() => Assert.Equal(2, _child.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Null(_child.NextResponseGate), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(_child.ReceivedMessages, IsSummarizer);
            Assert.Single(_child.ReceivedMessages, request => !IsSummarizer(request));
            Assert.False(observerRelease.Task.IsCompleted);
            Assert.False(_childRelease.Task.IsCompleted);
            _main.NextResponseGate = reviewRelease;
            _childRelease.TrySetResult();
            await AwaitAssertAsync(async () => Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.DeliveryAdmitted>()),
                Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var held = await ReadJournalAsync();
            Assert.Single(held.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Single(held.OfType<ChildRunEvent.ResultPrepared>());
            Assert.Empty(held.OfType<SessionCompacted>());
            Assert.DoesNotContain(held.OfType<ToolTaskAdopted>(), evt => evt.ContinuedChildRunId is not null);
            Assert.Equal(6, _main.CallCount);
            observerRelease.TrySetResult();
            var notice = Assert.IsType<CompactionOutput>(await subscriber.FishForMessageAsync<object>(
                message => message is CompactionOutput, Ceiling, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(notice.Summarized);
            Assert.True(notice.MessagesAfter < notice.MessagesBefore);
            await AwaitAssertAsync(() => Assert.Equal(7, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var after = await ReadJournalPositionsAsync();
            var compactedRow = Assert.Single(after, row => row.Event is SessionCompacted);
            var compacted = Assert.IsType<SessionCompacted>(compactedRow.Event);
            Assert.Equal(_child.ObservationResponseOverride, compacted.Summary);
            var adoptionRow = Assert.Single(after, row => row.Event is ToolTaskAdopted { ContinuedChildRunId: not null });
            Assert.True(compactedRow.SequenceNr < adoptionRow.SequenceNr);
            var adoption = Assert.IsType<ToolTaskAdopted>(adoptionRow.Event);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, adoption.TurnContext));
            var delivery = Assert.Single(after.Select(row => row.Event).OfType<ChildRunEvent.DeliveryAdmitted>());
            Assert.Equal(new[] { delivery.Input.InputId }, adoption.InputIds);
            AssertObligationProviderPair(delivery, _main.ReceivedMessages[^1]);
            Assert.Equal(owner, await OwnerAsync());
            Assert.Equal(2, _child.CallCount);
            Assert.Single(_child.ReceivedMessages, IsSummarizer);
            Assert.Single(_child.ReceivedMessages, request => !IsSummarizer(request));
            reviewRelease.TrySetResult();
            await CompletedAsync(subscriber);
            Assert.Equal(7, _main.CallCount);
        }
        finally
        {
            observerRelease.TrySetResult();
            reviewRelease.TrySetResult();
            snapshotRelease.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_actual_first_durable_completion_or_cancel_keeps_one_terminal_winner(bool cancelFirst)
    {
        var gate = InstallTerminalCutMailbox(nameof(BackgroundChildTerminal), child: false);
        var (owner, manager, subscriber) = await CreateObligationOwnerAsync();
        var controlRelease = NewSignal();
        try
        {
            await SendObligationInputAsync(manager, "original", "operator-a");
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedObligationTurnAsync(subscriber);
            var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
            _childRelease.TrySetResult();
            var captured = await gate.Held.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var completion = Assert.IsType<BackgroundChildTerminal>(captured.Envelope.Message);
            Assert.Equal(accepted.Run.RunId, completion.RunId);
            Assert.True(completion.Result.Success);
            if (!cancelFirst)
            {
                gate.Release();
                await CompletedObligationTurnAsync(subscriber);
                var committed = Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.TerminalRecorded>());
                Assert.True(committed.Terminal.Result.Success);
                Assert.Empty((await ReadJournalAsync()).OfType<ChildRunEvent.CancellationRequested>());
            }
            _start!.ControlCompletionGate = controlRelease;
            _main.PlannedResponses.Enqueue([ControlCall("race-cancel", accepted.Run.RunId)]);
            await SendObligationInputAsync(manager, "race-cancel", "operator-a");
            var reply = await _start.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.NotNull(reply.Run);
            Assert.Equal(accepted.Run.RunId, reply.Run.RunId);
            Assert.Equal(cancelFirst ? BackgroundChildState.Cancelling : BackgroundChildState.Completed, reply.Run.State);
            if (cancelFirst)
            {
                Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.CancellationRequested>());
                gate.Release();
            }
            await AssertTerminalCutOwnerBarrierAsync(owner, "after-competing-terminal");
            await AwaitAssertAsync(async () => Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.ResultPrepared>()),
                Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var rows = await ReadJournalPositionsAsync();
            var terminalRow = Assert.Single(rows, row => row.Event is ChildRunEvent.TerminalRecorded);
            var terminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
            Assert.Equal(terminalRow.SequenceNr, terminal.TerminalSequenceNr);
            Assert.Equal(!cancelFirst, terminal.Terminal.Result.Success);
            if (cancelFirst)
            {
                var cancellationRow = Assert.Single(rows, row => row.Event is ChildRunEvent.CancellationRequested);
                Assert.True(cancellationRow.SequenceNr < terminalRow.SequenceNr);
                Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Terminal.Result.Completion);
                Assert.Equal(SubAgentRunOutcome.Failed, terminal.Terminal.Result.Outcome);
                Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
                var storage = Assert.IsType<ToolSessionScope.Bound>(_start.Prepared!.Execution.Scope.Authority.Session).Storage;
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                    Path.Combine(storage.ArtifactDirectory.Value, "cancelled-results.json"), TestContext.Current.CancellationToken));
                Assert.Equal(accepted.Run.RunId.Value, report.RootElement.GetProperty("run_id").GetString());
                Assert.Equal("Cancelled", report.RootElement.GetProperty("state").GetString());
                Assert.Equal("Recorded receipts describe known local results. They do not prove external effects stopped.",
                    report.RootElement.GetProperty("external_effects").GetString());
            }
            else
            {
                Assert.DoesNotContain(rows, row => row.Event is ChildRunEvent.CancellationRequested or ChildRunEvent.DispatchClosed);
                Assert.IsType<ChildRunCompletion.Completed>(terminal.Terminal.Result.Completion);
                Assert.Equal(completion.Result.Output, terminal.Terminal.Result.Output);
            }
            controlRelease.TrySetResult();
            await CompletedObligationTurnAsync(subscriber);
            await AwaitAssertAsync(async () =>
            {
                var final = await ReadJournalAsync();
                Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Single(final.OfType<ChildRunEvent.ResultPrepared>());
                var delivery = Assert.Single(final.OfType<ChildRunEvent.DeliveryAdmitted>());
                Assert.Single(final.OfType<TurnRecorded>(), turn => turn.ConsumedInputIds.Contains(delivery.Input.InputId));
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, _child.CallCount);
        }
        finally { gate.Release(); controlRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Child_idle_obligations_defer_owner_loss_then_allow_ordinary_passivation()
    {
        var (owner, manager, subscriber) = await CreateObligationOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedObligationTurnAsync(subscriber);
        var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        var enrichmentRelease = new TaskCompletionSource<WorkingContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewRelease = NewSignal();
        var control = CreateTestProbe();
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            owner.Tell(new LeaveSession(subscriber) { SessionId = Session }, control.Ref);
            _time.Advance(TimeSpan.FromMinutes(31));
            owner.Tell(ReceiveTimeout.Instance, control.Ref);
            owner.Tell(new Identify("live-child-idle-obligation"), control.Ref);
            Assert.Equal(owner, (await control.ExpectMsgAsync<ActorIdentity>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken)).Subject);
            Assert.Equal(owner, await OwnerAsync());
            Assert.Null(await ReadDrainedChildSnapshotAsync());
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(2, _main.CallCount);

            _snapshots.InvocationStarted = NewSignal();
            _snapshots.Pending = enrichmentRelease;
            _childRelease.TrySetResult();
            await _snapshots.InvocationStarted.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var terminalOnly = await ReadJournalAsync();
            Assert.Single(terminalOnly.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Empty(terminalOnly.OfType<ChildRunEvent.ResultPrepared>());
            Assert.Empty(terminalOnly.OfType<ChildRunEvent.DeliveryAdmitted>());
            _time.Advance(TimeSpan.FromMinutes(31));
            owner.Tell(ReceiveTimeout.Instance, control.Ref);
            owner.Tell(new Identify("undelivered-child-idle-obligation"), control.Ref);
            Assert.Equal(owner, (await control.ExpectMsgAsync<ActorIdentity>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken)).Subject);
            Assert.Null(await ReadDrainedChildSnapshotAsync());
            _main.NextResponseGate = reviewRelease;
            enrichmentRelease.TrySetResult(new WorkingContextSnapshot
            { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() });
            await AwaitAssertAsync(() => Assert.Equal(3, _main.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(owner, await OwnerAsync());
            var delivery = Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.DeliveryAdmitted>());
            AssertObligationProviderPair(delivery, _main.ReceivedMessages[^1]);
            var reviewSubscriber = CreateTestProbe();
            manager.Tell(new JoinSession(reviewSubscriber)
            { SessionId = Session, Filter = OutputFilter.Full | OutputFilter.ProcessingState }, reviewSubscriber.Ref);
            await reviewSubscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            reviewRelease.TrySetResult();
            await CompletedObligationTurnAsync(reviewSubscriber);
            var settled = await ReadJournalAsync();
            Assert.Single(settled.OfType<TurnRecorded>(), turn => turn.ConsumedInputIds.Contains(delivery.Input.InputId));
            owner.Tell(new LeaveSession(reviewSubscriber) { SessionId = Session }, control.Ref);
            _time.Advance(TimeSpan.FromMinutes(31));
            owner.Tell(ReceiveTimeout.Instance, control.Ref);
            await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            SessionSnapshot? snapshot = null;
            await AwaitAssertAsync(async () =>
            {
                snapshot = await ReadDrainedChildSnapshotAsync();
                Assert.NotNull(snapshot);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var stored = Assert.Single(snapshot!.ChildRuns);
            Assert.Equal(accepted.Run.RunId, stored.RunId);
            Assert.Equal(delivery.Input.InputId, stored.DeliveryInputId);
            Assert.True(stored.Terminal!.Result.Success);
            Assert.Empty(snapshot.PendingInputs);
            Assert.Equal(3, _main.CallCount);
            Assert.Equal(1, _child.CallCount);
        }
        finally
        {
            _snapshots.Pending = null;
            enrichmentRelease.TrySetResult(new WorkingContextSnapshot
            { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() });
            reviewRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task Two_held_child_providers_do_not_block_parent_status_or_cancel_the_other_run()
    {
        _main.ToolCallsOnFirstCall!.Add(new FunctionCallContent("start-2", "start_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Delegate the second independent part." }));
        var (_, manager, subscriber) = await CreateObligationOwnerAsync();
        await SendObligationInputAsync(manager, "original", "operator-a");
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await _secondChild.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedObligationTurnAsync(subscriber);
        var accepted = (await ReadJournalAsync()).OfType<ChildRunAccepted>().ToArray();
        Assert.Equal(2, accepted.Length);
        var first = Assert.Single(accepted, admission => Assert.IsType<ChildRunStartKey.Tool>(admission.Run.StartKey).CallId.Value == "start-1");
        var second = Assert.Single(accepted, admission => Assert.IsType<ChildRunStartKey.Tool>(admission.Run.StartKey).CallId.Value == "start-2");
        await CheckAsync(first.Run.RunId, "held-first-status", false, "Running");
        Assert.False(_childRelease.Task.IsCompleted);
        Assert.False(_secondChildRelease.Task.IsCompleted);
        await CheckAsync(first.Run.RunId, "held-first-cancel", true, "Cancelling");
        await AwaitAssertAsync(async () =>
        {
            var records = await ReadJournalAsync();
            var terminal = Assert.Single(records.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Equal(first.Run.RunId, terminal.RunId);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
            var delivery = Assert.Single(records.OfType<ChildRunEvent.DeliveryAdmitted>());
            Assert.Single(records.OfType<TurnRecorded>(), turn => turn.ConsumedInputIds.Contains(delivery.Input.InputId));
        }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await AssertTerminalCutOwnerBarrierAsync(await OwnerAsync(), "after-first-child-settlement");
        await CheckAsync(second.Run.RunId, "held-second-status", false, "Running");
        var final = await ReadJournalAsync();
        Assert.Equal(first.Run.RunId, Assert.Single(final.OfType<ChildRunEvent.CancellationRequested>()).RunId);
        Assert.Equal(first.Run.RunId, Assert.Single(final.OfType<ChildRunEvent.DispatchClosed>()).RunId);
        Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.Equal(2, final.OfType<ChildRunAccepted>().Count());
        Assert.Equal(2, final.OfType<ChildRunEvent.Started>().Count());
        Assert.False(_secondChildRelease.Task.IsCompleted);
        Assert.Equal(1, _child.CallCount);
        Assert.Equal(1, _secondChild.CallCount);

        async Task CheckAsync(SubAgentRunId runId, string id, bool cancel, string expectedState)
        {
            _main.PlannedResponses.Enqueue([new FunctionCallContent(id, "status_json_probe",
                new Dictionary<string, object?> { ["run_id"] = runId.Value, ["cancel"] = cancel,
                    ["_rationale"] = "Inspect the specific accepted child." })]);
            await SendObligationInputAsync(manager, id, "operator-a");
            var output = Assert.IsType<ToolResultOutput>(await subscriber.FishForMessageAsync<object>(
                message => message is ToolResultOutput result && result.CallId.Value == id,
                Ceiling, cancellationToken: TestContext.Current.CancellationToken));
            using var body = JsonDocument.Parse(output.Result);
            Assert.Equal(runId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(expectedState, body.RootElement.GetProperty("state").GetString());
            await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
                Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            var result = Assert.Single((await ReadJournalAsync()).OfType<ToolCallRecorded>(),
                record => record.ToolResult.ToolCallId == new ToolCallId(id));
            Assert.Equal(output.Result, result.ToolResult.Content);
        }
    }

    private async Task<(IActorRef Owner, IActorRef Manager, Akka.TestKit.TestProbe Subscriber)> CreateObligationOwnerAsync()
    {
        var created = await CreateOwnerAsync();
        created.Manager.Tell(new JoinSession(created.Subscriber)
        { SessionId = Session, Filter = OutputFilter.Full | OutputFilter.ProcessingState }, created.Subscriber.Ref);
        await created.Subscriber.ExpectMsgAsync<SessionJoined>(Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        return created;
    }

    private static async Task CompletedObligationTurnAsync(Akka.TestKit.TestProbe subscriber)
    {
        await CompletedAsync(subscriber);
        await subscriber.FishForMessageAsync<object>(message => message is ProcessingStateOutput { IsProcessing: false },
            Ceiling, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Task<CommandAck> SendObligationInputAsync(IActorRef manager, string turn, string requester)
        => manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = Session, Content = "Complete the neutral request.", Source = DeliverySource(turn, requester)
        }, Ceiling, TestContext.Current.CancellationToken);

    private static void AssertObligationProviderPair(ChildRunEvent.DeliveryAdmitted delivery, IReadOnlyList<ChatMessage> request)
    {
        var id = delivery.Input.UserMessage.ToolCallId!.Value.Value;
        var call = Assert.Single(request.SelectMany(message => message.Contents.OfType<FunctionCallContent>()), item => item.CallId == id);
        Assert.Equal(delivery.Input.UserMessage.Name, call.Name);
        Assert.Equal(delivery.Input.UserMessage.Content, Assert.IsType<string>(Assert.Single(
            request.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), item => item.CallId == id).Result));
    }
}
