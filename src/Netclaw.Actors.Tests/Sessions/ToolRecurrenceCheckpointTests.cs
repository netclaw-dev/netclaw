// -----------------------------------------------------------------------
// <copyright file="ToolRecurrenceCheckpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Protobuf;
using Microsoft.Extensions.AI;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolRecurrenceCheckpointTests
{
    [Fact]
    public void Live_and_replayed_evidence_produce_identical_checkpoint_bytes()
    {
        var live = new TurnStateTracker();
        var state = SessionState.Empty;
        for (var round = 1; round <= 2; round++)
        {
            var batch = Prepare($"round-{round}");
            var decision = live.EvaluateBeforeDispatch(batch);
            Assert.Equal(ToolCycleDecisionKind.Execute, decision.Kind);
            var admission = Admission(batch, decision);
            var started = new ToolBatchStarted
            {
                SessionId = new SessionId("checkpoint/test"),
                LoopAdmission = admission,
                LoopDelta = live.CaptureDelta("task", state.LoopCheckpoint)
            };
            state = state.ApplyLoopAdmission(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(started)));
            var receipt = new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure);
            const string text = "The same exact failure.";
            var recorded = new ToolCallRecorded
            {
                SessionId = new SessionId("checkpoint/test"),
                LoopObservation = ToolCycleSignatureFactory.CreateObservation($"round-{round}", receipt, text, false)
            };
            state = state.ApplyLoopObservation(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(recorded)));
            live.ObserveCompleted(ToolCycleSignatureFactory.Complete(batch, new Dictionary<string, ToolCycleResult>
            {
                [$"round-{round}"] = new(receipt.Category, text)
            }));
            Assert.Equal(NetclawProtoMapper.ToProto(live.CaptureCheckpoint("task")).ToByteArray(),
                NetclawProtoMapper.ToProto(state.LoopCheckpoint).ToByteArray());
        }

        var repeated = Prepare("corrected");
        var correction = live.EvaluateBeforeDispatch(repeated);
        Assert.Equal(ToolCycleDecisionKind.Correct, correction.Kind);
        state = state.ApplyLoopAdmission(new ToolBatchStarted
        {
            LoopAdmission = Admission(repeated, correction),
            LoopDelta = live.CaptureDelta("task", state.LoopCheckpoint)
        });
        state = state.ApplyLoopObservation(new ToolCallRecorded
        {
            LoopObservation = ToolCycleSignatureFactory.CreateObservation("corrected",
                new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle), "This call did not execute.", true)
        });
        var snapshot = NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(state.ToSnapshot()));
        var restored = new TurnStateTracker();
        restored.RestoreCheckpoint(SessionState.FromSnapshot(snapshot).LoopCheckpoint);
        Assert.Equal(ToolCycleDecisionKind.Stop, restored.EvaluateBeforeDispatch(Prepare("later")).Kind);
    }

    [Fact]
    public void Missing_receipt_suffix_preserves_suspicion_and_exposes_failure()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("actual");
        var decision = tracker.EvaluateBeforeDispatch(batch);
        var state = SessionState.Empty.ApplyLoopAdmission(new ToolBatchStarted
        {
            LoopAdmission = Admission(batch, decision),
            LoopDelta = tracker.CaptureDelta("task", new ToolLoopCheckpoint())
        });
        state = state.ApplyLoopObservation(new ToolCallRecorded
        {
            LoopObservation = ToolCycleSignatureFactory.CreateObservation("actual", null, "An unverified result.", false)
        });
        var restored = SessionState.FromSnapshot(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(state.ToSnapshot())));
        Assert.True(restored.LoopReceiptFailure);
        Assert.Empty(restored.LoopCheckpoint.Entries);
    }

    [Fact]
    public void Pending_fact_cannot_exempt_another_tool_identity()
    {
        var tracker = new TurnStateTracker();
        var batch = Prepare("first");
        var outcome = ToolCycleSignatureFactory.Complete(batch, new Dictionary<string, ToolCycleResult>
        {
            ["first"] = new(ToolInvocationOutcomeCategory.Success, "An unchanged result.") { PendingJob = true }
        });
        tracker.ObserveCompleted(outcome);
        tracker.ObserveCompleted(outcome);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(Prepare("third")).Kind);
    }

    [Fact]
    public void Adoption_survives_input_consumption_and_terminal_events_close_the_task()
    {
        var session = new SessionId("checkpoint/adoption");
        var canonical = TurnContext.FromMessageSource(session, new TurnId("first-task"), null).ToRecord();
        var input = new InputAdmitted
        {
            SessionId = session, InputId = InputId.New(), TurnContext = canonical,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Read one file." }
        };
        var state = SessionState.Empty.Apply(input).Apply(new ToolTaskAdopted
        {
            SessionId = session, TurnContext = canonical, InputIds = [input.InputId]
        });
        state = state.CloseInputs([input.InputId]);
        var restored = SessionState.FromSnapshot(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(state.ToSnapshot())));
        Assert.Empty(restored.PendingInputs);
        Assert.Equal("first-task", restored.AdoptedTaskContext!.TurnId);
        Assert.Equal("first-task", restored.LoopCheckpoint.TaskId);
        Assert.Equal([input.InputId], restored.AdoptedTaskInputIds);

        var unrelated = restored.Apply(new InputClosed { SessionId = session, InputIds = [InputId.New()] });
        Assert.NotNull(unrelated.AdoptedTaskContext);
        var final = restored.Apply(new InputClosed { SessionId = session, TaskId = "first-task" });
        Assert.Null(final.AdoptedTaskContext);
        Assert.Empty(final.AdoptedTaskInputIds);
        var completed = restored.Apply(new TurnRecorded
        {
            SessionId = session, UserMessage = input.UserMessage,
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "The file is available." }
        });
        Assert.Null(completed.AdoptedTaskContext);
    }

    [Fact]
    public void Fresh_admission_preserves_active_evidence_until_durable_adoption()
    {
        var session = new SessionId("checkpoint/fresh-adoption");
        var first = TurnContext.FromMessageSource(session, new TurnId("first-task"), null).ToRecord();
        var next = TurnContext.FromMessageSource(session, new TurnId("next-task"), null).ToRecord();
        var oldInput = new InputAdmitted
        {
            SessionId = session, InputId = InputId.New(), TurnContext = first,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Read the old file." }
        };
        var freshInput = oldInput with { InputId = InputId.New(), TurnContext = next };
        var state = SessionState.Empty.Apply(oldInput).Apply(new ToolTaskAdopted
        { SessionId = session, TurnContext = first, InputIds = [oldInput.InputId] });
        state = state with { LoopReceiptFailure = true };
        state = state.Apply(freshInput);
        Assert.Equal("first-task", state.LoopCheckpoint.TaskId);
        Assert.True(state.LoopReceiptFailure);
        Assert.Equal("first-task", state.AdoptedTaskContext!.TurnId);
        var adopted = NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(new ToolTaskAdopted
        { SessionId = session, TurnContext = next, InputIds = [freshInput.InputId] }));
        state = state.CloseInputs([oldInput.InputId]).Apply(adopted);
        Assert.Equal("next-task", state.LoopCheckpoint.TaskId);
        Assert.False(state.LoopReceiptFailure);
        Assert.Single(state.PendingInputs);
        Assert.Equal([freshInput.InputId], state.AdoptedTaskInputIds);
    }

    [Fact]
    public void Legacy_metadata_baseline_retains_authority_and_does_not_reset_after_a_duplicate()
    {
        var session = new SessionId("checkpoint/legacy");
        var canonical = TurnContext.FromMessageSource(session, new TurnId("legacy-task"), null).ToRecord();
        var admission = new ToolLoopAdmission
        {
            TaskId = canonical.TurnId, ActionHash = "prepared-action",
            Calls = new[] { new ToolLoopPreparedCall("unanswered", "probe", "prepared-args", false) }
        };
        var baseline = new ToolBatchStarted
        {
            SessionId = session, MetadataOnly = true, LegacyTaskContext = canonical, LoopAdmission = admission,
            LoopDelta = new ToolLoopDelta { TaskId = canonical.TurnId, Reset = true }
        };
        var original = new SerializableChatMessage { Role = ChatRole.User, Content = "Read the original value." };
        var state = SessionState.Empty with { History = SessionState.Empty.History.Add(original) };
        state = state.ApplyLoopAdmission(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(baseline)));
        Assert.Equal(original, Assert.Single(state.History));
        Assert.True(SessionState.SameCanonicalContext(canonical, state.AdoptedTaskContext!));
        Assert.Empty(state.AdoptedTaskInputIds);
        state = state.ApplyLoopObservation(new ToolCallRecorded
        {
            SessionId = session, LoopObservation = ToolCycleSignatureFactory.CreateObservation("unanswered",
                new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure), "Known exact failure.", false)
        });
        state = state with { LoopCheckpoint = state.LoopCheckpoint with { LastBlockedAction = "retained-correction" } };
        var restored = SessionState.FromSnapshot(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(state.ToSnapshot())));
        var retry = restored.ApplyLoopAdmission(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(baseline)));
        Assert.Equal(NetclawProtoMapper.ToProto(restored.ToSnapshot()).ToByteArray(),
            NetclawProtoMapper.ToProto(retry.ToSnapshot()).ToByteArray());
        Assert.True(SessionState.SameCanonicalContext(canonical, retry.AdoptedTaskContext!));
        Assert.Equal("retained-correction", retry.LoopCheckpoint.LastBlockedAction);
        Assert.Throws<InvalidDataException>(() => restored.ApplyLoopAdmission(baseline with
        { LegacyTaskContext = canonical with { SourceScope = "forged-scope" } }));
        Assert.Throws<InvalidDataException>(() => restored.ApplyLoopAdmission(baseline with
        { ConsumedInputIds = new[] { InputId.New() } }));
        Assert.Throws<InvalidDataException>(() => (SessionState.Empty with
        { LoopCheckpoint = new ToolLoopCheckpoint { TaskId = "existing-new-task" } }).ApplyLoopAdmission(baseline));
    }

    private static PreparedToolCycleBatch Prepare(string id)
        => ToolCycleSignatureFactory.Prepare([new FunctionCallContent(id, "probe", new Dictionary<string, object?>
        { ["value"] = 1, ["_rationale"] = "Read one exact value." })], new SignatureExecutor());

    private static ToolLoopAdmission Admission(PreparedToolCycleBatch batch, ToolCycleBatchDecision decision)
        => new()
        {
            TaskId = "task", ActionHash = batch.Action.Value,
            Calls = batch.Calls.Select(call => new ToolLoopPreparedCall(call.CallId.Value, call.ToolName.Value,
                call.ArgumentsHash, call.AllowsPendingJob)).ToArray(),
            RefusedCallIds = decision.RefusedCallIds.ToArray()
        };

    private sealed class SignatureExecutor : IToolExecutor
    {
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("The signature test must not execute a tool.");
    }
}
