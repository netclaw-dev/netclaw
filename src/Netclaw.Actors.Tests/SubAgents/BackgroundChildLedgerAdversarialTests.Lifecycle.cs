// -----------------------------------------------------------------------
// <copyright file="BackgroundChildLedgerAdversarialTests.Lifecycle.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildLedgerAdversarialTests
{
    [Theory]
    [InlineData("event-session")]
    [InlineData("event-run")]
    [InlineData("result-run")]
    [InlineData("result-scope")]
    [InlineData("result-agent")]
    public void A_terminal_fact_cannot_replace_the_accepted_owner(string fault)
    {
        var (state, run) = AcceptedSlash(receiptFailure: false);
        var terminal = Terminal(run);
        var evt = new ChildRunEvent.TerminalRecorded(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123457 };
        evt = fault switch
        {
            "event-session" => evt with { SessionId = new SessionId("signalr/foreign") },
            "event-run" => evt with { RunId = new SubAgentRunId("foreign") },
            "result-run" => evt with { Terminal = terminal with { Result = terminal.Result with { RunId = new SubAgentRunId("foreign") } } },
            "result-scope" => evt with { Terminal = terminal with { Result = terminal.Result with { ScopeId = new SubAgentScopeId("foreign") } } },
            "result-agent" => evt with { Terminal = terminal with { Result = terminal.Result with { AgentName = new AgentName("foreign") } } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        RejectUnchanged(state, () => state.Apply(RoundTrip(evt)));
        var valid = state.Apply(RoundTrip(new ChildRunEvent.TerminalRecorded(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123457 }));
        Assert.Equal(terminal.Result.Output, valid.ChildRuns[run.RunId].Terminal!.Result.Output);
        RejectUnchanged(valid, () => valid.Apply(new ChildRunEvent.CancellationRequested
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123458 }));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("authority")]
    [InlineData("source-run")]
    [InlineData("dual-lineage")]
    [InlineData("input-id")]
    [InlineData("used-call-id")]
    public void Only_the_atomic_canonical_child_result_can_admit_a_continuation(string fault)
    {
        var (state, run) = PreparedSlash(receiptFailure: true);
        var input = Delivery(run, "framework-result");
        if (fault == "used-call-id")
            state = state with { History = state.History.Add(ChildRunDelivery.Call(run, input.UserMessage.ToolCallId!.Value)) };
        var invalid = fault switch
        {
            "body" => input with { UserMessage = input.UserMessage with { Content = "A fabricated successful result." } },
            "authority" => input with { TurnContext = input.TurnContext with { RequesterSenderId = new SenderId("operator-b") } },
            "source-run" => input with { SourceChildRunId = new SubAgentRunId("foreign") },
            "dual-lineage" => input with { SourceBackgroundJobId = new BackgroundJobId("foreign-job") },
            "input-id" => input with { InputId = new InputId("foreign-input") },
            "used-call-id" => input,
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        RejectUnchanged(state, () => state.Apply(invalid));
        RejectUnchanged(state, () => state.Apply(new ChildRunEvent.DeliveryAdmitted(invalid)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 }));
        var validInput = Delivery(run, "unused-framework-call");
        var valid = state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(validInput)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 }));
        Assert.Equal(validInput.InputId, Assert.Single(valid.PendingInputs).InputId);
        Assert.Equal(validInput.InputId, valid.ChildRuns[run.RunId].DeliveryInputId);
        RejectUnchanged(valid, () => valid.Apply(new ChildRunEvent.DeliveryAdmitted(validInput)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123461 }));
        var restored = SessionState.FromSnapshot(RoundTrip(valid.ToSnapshot()));
        Assert.Equal(validInput.UserMessage.Content, Assert.Single(restored.PendingInputs).UserMessage.Content);
        Assert.True(restored.ChildRuns[run.RunId].ParentReceiptFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_turn_continuation_restores_detector_evidence_before_the_duplicate_adoption_path(bool receiptFailure)
    {
        var (state, run) = PreparedSlash(receiptFailure);
        var input = Delivery(run, "framework-result");
        state = state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(input)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 }));
        // The authority turn stays unchanged, but the current detector state differs from the retained task.
        state = state with { LoopCheckpoint = new ToolLoopCheckpoint { TaskId = "different-current-task" }, LoopReceiptFailure = !receiptFailure };
        state = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        var adoption = new ToolTaskAdopted
        { SessionId = Owner, TurnContext = run.OriginalContext, InputIds = [input.InputId], ContinuedChildRunId = run.RunId };
        RejectUnchanged(state, () => state.Apply(adoption with { ContinuedChildRunId = null }));
        RejectUnchanged(state, () => state.Apply(adoption with { ContinuedJobKey = "foreign-job" }));
        var restored = state.Apply(RoundTrip(adoption));
        Assert.True(BackgroundChildRun.SameCheckpoint(run.ParentCheckpoint, restored.LoopCheckpoint));
        Assert.Equal(receiptFailure, restored.LoopReceiptFailure);
        Assert.True(SessionState.SameCanonicalContext(run.OriginalContext, restored.AdoptedTaskContext!));
        Assert.Equal(new[] { input.InputId }, restored.AdoptedTaskInputIds);
        Assert.Null(restored.LoopAdmission);
        Assert.Empty(restored.LoopObservations);
        Assert.Equal(input.UserMessage.Content, Assert.Single(restored.PendingInputs).UserMessage.Content);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("task")]
    [InlineData("checkpoint")]
    [InlineData("receipt-failure")]
    public void Child_results_coalesce_only_with_the_same_original_task_and_detector_evidence(string fault)
    {
        var (state, first) = PreparedSlash(receiptFailure: true);
        var second = first with
        {
            RunId = new SubAgentRunId("sibling-run"), ScopeId = new SubAgentScopeId($"{Owner.Value}/subagent/worker/sibling-run"),
            StartKey = new ChildRunStartKey.Slash(new InputId("origin-two")) { SessionId = Owner, TurnId = new TurnId(first.OriginalContext.TurnId) },
            Terminal = null, PreparedTerminal = null
        };
        var secondTerminal = Terminal(second);
        second = second with { Terminal = secondTerminal, TerminalSequenceNr = 2, PreparedTerminal = secondTerminal };
        // This snapshot premise tests the continuation consumer independently from live owner refresh.
        var incompatible = fault switch
        {
            "authority" => second with { OriginalContext = second.OriginalContext with { RequesterSenderId = new SenderId("operator-b") } },
            "task" => second with { ParentCheckpoint = second.ParentCheckpoint with { TaskId = "other-original-task" } },
            "checkpoint" => second with { ParentCheckpoint = second.ParentCheckpoint with { LastBlockedAction = "other-correction" } },
            "receipt-failure" => second with { ParentReceiptFailure = false },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        SessionState AdmitPair(BackgroundChildRun sibling)
        {
            var pair = state with { ChildRuns = state.ChildRuns.Add(sibling.RunId, sibling) };
            foreach (var run in new[] { first, sibling })
                pair = pair.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(Delivery(run, $"framework-{run.RunId.Value}"))
                { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 }));
            return SessionState.FromSnapshot(RoundTrip(pair.ToSnapshot()));
        }
        ToolTaskAdopted AdoptPair(SessionState pair) => new()
        { SessionId = Owner, TurnContext = first.OriginalContext, InputIds = pair.PendingInputs.Select(input => input.InputId).ToArray(), ContinuedChildRunId = second.RunId };
        var invalid = AdmitPair(incompatible);
        RejectUnchanged(invalid, () => invalid.Apply(AdoptPair(invalid)));
        var valid = AdmitPair(second);
        var adopted = valid.Apply(RoundTrip(AdoptPair(valid)));
        Assert.Equal(2, adopted.AdoptedTaskInputIds.Count);
        Assert.True(BackgroundChildRun.SameCheckpoint(first.ParentCheckpoint, adopted.LoopCheckpoint));
        Assert.True(adopted.LoopReceiptFailure);
        Assert.Equal(new[] { first.RunId, second.RunId }, adopted.PendingInputs.Select(input => input.SourceChildRunId!.Value));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("role")]
    [InlineData("name")]
    [InlineData("missing-call")]
    [InlineData("empty-call")]
    [InlineData("original-start-call")]
    public void A_snapshot_cannot_replace_the_canonical_pending_child_result(string fault)
    {
        var (state, run) = Acceptance(Owner, slash: false);
        state = state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Owner, Run = run }));
        state = state.ApplyLoopObservation(new ToolCallRecorded
        {
            SessionId = Owner,
            LoopObservation = ToolCycleSignatureFactory.CreateObservation("start-call", new ToolInvocationReceipt.Succeeded([], null), "accepted", false)
        }).CloseInputs(run.OriginInputIds);
        run = state.ChildRuns[run.RunId];
        Assert.True(run.StartBatchSettled);
        var terminal = Terminal(run);
        state = state.Apply(RoundTrip(new ChildRunEvent.TerminalRecorded(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123458 }));
        state = state.Apply(RoundTrip(new ChildRunEvent.ResultPrepared(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123459 }));
        run = state.ChildRuns[run.RunId];
        var input = Delivery(run, "framework-result");
        state = state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(input)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 }));
        var snapshot = state.ToSnapshot();
        var changed = fault switch
        {
            "body" => input.UserMessage with { Content = "A forged success replaces the recorded result." },
            "role" => input.UserMessage with { Role = ChatRole.User },
            "name" => input.UserMessage with { Name = "shell" },
            "missing-call" => input.UserMessage with { ToolCallId = null },
            "empty-call" => input.UserMessage with { ToolCallId = new ToolCallId("") },
            "original-start-call" => input.UserMessage with { ToolCallId = new ToolCallId("start-call") },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var corrupted = RoundTrip(snapshot with { PendingInputs = [input with { UserMessage = changed }] });
        Assert.Throws<InvalidDataException>(() => SessionState.FromSnapshot(corrupted));
        var valid = SessionState.FromSnapshot(RoundTrip(snapshot));
        var restoredMessage = Assert.Single(valid.PendingInputs).UserMessage;
        Assert.Equal(input.UserMessage.Content, restoredMessage.Content);
        Assert.Equal(input.UserMessage.Role, restoredMessage.Role);
        Assert.Equal(input.UserMessage.Name, restoredMessage.Name);
        Assert.Equal(input.UserMessage.ToolCallId, restoredMessage.ToolCallId);
        var adopted = valid.Apply(RoundTrip(new ToolTaskAdopted
        { SessionId = Owner, TurnContext = run.OriginalContext, InputIds = [input.InputId], ContinuedChildRunId = run.RunId }));
        Assert.True(BackgroundChildRun.SameCheckpoint(run.ParentCheckpoint, adopted.LoopCheckpoint));
        Assert.Equal(input.UserMessage.Content, Assert.Single(adopted.PendingInputs).UserMessage.Content);
        Assert.Equal("framework-result", Assert.Single(adopted.PendingInputs).UserMessage.ToolCallId!.Value.Value);
    }

    private (SessionState State, BackgroundChildRun Run) AcceptedSlash(bool receiptFailure)
    {
        var (state, run) = Acceptance(Owner, slash: true);
        state = state with { LoopReceiptFailure = receiptFailure };
        run = run with { ParentReceiptFailure = receiptFailure };
        return (state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Owner, Run = run })).CloseInputs(run.OriginInputIds), run);
    }

    private (SessionState State, BackgroundChildRun Run) PreparedSlash(bool receiptFailure)
    {
        var (state, run) = AcceptedSlash(receiptFailure);
        var terminal = Terminal(run);
        state = state.Apply(RoundTrip(new ChildRunEvent.TerminalRecorded(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123458 }));
        state = state.Apply(RoundTrip(new ChildRunEvent.ResultPrepared(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123459 }));
        return (state, state.ChildRuns[run.RunId]);
    }

    private static ChildRunTerminal Terminal(BackgroundChildRun run) => new(new SubAgentResult
    {
        Completion = new ChildRunCompletion.Completed(new WorkingContextDelta { ReadFiles = ["/neutral/confirmed.txt"] }),
        Output = $"The child {run.RunId.Value} confirmed its result.", RunId = run.RunId, ScopeId = run.ScopeId, AgentName = run.AgentName
    }, false, null);

    private static InputAdmitted Delivery(BackgroundChildRun run, string callId) => new()
    {
        SessionId = Owner, InputId = new InputId($"child-result-{run.RunId.Value}"), SourceChildRunId = run.RunId,
        TurnContext = run.OriginalContext,
        UserMessage = new SerializableChatMessage
        { Role = ChatRole.Tool, Name = ChildRunDelivery.ToolName(run), ToolCallId = new ToolCallId(callId), Content = ChildRunDelivery.Body(run) }
    };

    private void RejectUnchanged(SessionState state, Func<SessionState> apply)
    {
        var before = Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot());
        Assert.Throws<InvalidDataException>(() => apply());
        Assert.Equal(before, Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot()));
    }
}
