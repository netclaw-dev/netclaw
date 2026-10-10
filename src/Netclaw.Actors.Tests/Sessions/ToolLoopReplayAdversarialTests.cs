// -----------------------------------------------------------------------
// <copyright file="ToolLoopReplayAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolLoopReplayAdversarialTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly SessionId Session = new("adversarial/replay");
    private static readonly FakeToolExecutor Executor = new();

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Snapshot_at_each_result_cut_preserves_the_suffix_and_counts_the_feedback_round_once(int cut)
    {
        var batch = Prepare();
        var state = Admit(SessionState.Empty, batch, "original-admitted-turn", []);
        var events = batch.Calls.Select(call => Result(call, new ToolInvocationReceipt.Succeeded([], null), "same actual result")).ToArray();
        for (var index = 0; index < cut; index++)
            state = state.ApplyLoopObservation(RoundTrip(events[index]));

        var recovered = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.Equal("original-admitted-turn", recovered.LoopAdmission!.TaskId);
        Assert.Equal(cut, recovered.LoopObservations.Count);
        if (cut < events.Length)
            Assert.Empty(recovered.LoopCheckpoint.Entries);
        foreach (var item in events)
            recovered = recovered.ApplyLoopObservation(RoundTrip(item));
        Assert.Equal(3, recovered.LoopObservations.Count);
        Assert.Equal(1, recovered.LoopCheckpoint.Entries.Single(entry => entry.ToolName == "probe").EqualRounds);

        recovered = Admit(recovered, batch, "original-admitted-turn", []);
        foreach (var item in events.Reverse())
            recovered = recovered.ApplyLoopObservation(RoundTrip(item));
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(recovered.LoopCheckpoint);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
        Assert.Equal("original-admitted-turn", recovered.LoopCheckpoint.TaskId);
    }

    [Fact]
    public void A_durable_missing_receipt_retains_its_defect_and_never_supplies_a_successful_observation()
    {
        var batch = Prepare();
        var tracker = new TurnStateTracker();
        var completed = ToolCycleSignatureFactory.Complete(batch, batch.Calls.ToDictionary(call => call.CallId.Value,
            _ => new ToolCycleResult(ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);
        var prior = SessionState.Empty with { LoopCheckpoint = tracker.CaptureCheckpoint("original-admitted-turn") };
        var state = Admit(prior, batch, "original-admitted-turn", []);
        var missing = Result(batch.Calls[0], null, "A completed actual result lacks its receipt.");
        state = state.ApplyLoopObservation(RoundTrip(missing));
        var recovered = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));

        Assert.True(recovered.LoopReceiptFailure);
        Assert.Single(recovered.LoopObservations);
        Assert.True(recovered.LoopObservations[0].MissingReceipt);
        Assert.Equal("original-admitted-turn", recovered.LoopAdmission!.TaskId);
        Assert.Equal(2, recovered.LoopCheckpoint.Entries.Single(entry => entry.ToolName == "probe").EqualRounds);
        Assert.Equal(missing.ToolResult.Content, RoundTrip(missing).ToolResult.Content);
    }

    [Fact]
    public void A_synthetic_observation_flag_cannot_replace_the_admissions_refused_call_identifiers()
    {
        var batch = Prepare();
        var state = SessionState.Empty;
        for (var round = 0; round < 2; round++)
        {
            state = Admit(state, batch, "original-admitted-turn", []);
            foreach (var call in batch.Calls)
            {
                var actual = Result(call, new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle), "tool-owned advice");
                // The admitted actual call determines provenance, even if this flag is wrong.
                actual = actual with { LoopObservation = actual.LoopObservation! with { Synthetic = true } };
                state = state.ApplyLoopObservation(RoundTrip(actual));
            }
        }
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(state.LoopCheckpoint);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
    }

    [Fact]
    public void A_refused_group_stays_corrected_when_only_its_unrelated_sibling_completes_actual_work()
    {
        var batch = Prepare();
        var tracker = new TurnStateTracker();
        var completed = ToolCycleSignatureFactory.Complete(batch, batch.Calls.ToDictionary(call => call.CallId.Value,
            _ => new ToolCycleResult(ToolInvocationOutcomeCategory.Success, "same")));
        tracker.ObserveCompleted(completed);
        tracker.ObserveCompleted(completed);
        var mixed = ToolCycleSignatureFactory.Prepare(
            [Call("matched", "probe", 1), Call("fresh", "unrelated", 99)], Executor);
        var prior = SessionState.Empty with { LoopCheckpoint = tracker.CaptureCheckpoint("original-admitted-turn") };
        var state = Admit(prior, mixed, "original-admitted-turn", ["matched"]);
        state = state.ApplyLoopObservation(RoundTrip(Result(mixed.Calls[1], new ToolInvocationReceipt.Succeeded([], null), "fresh result")));
        state = state.ApplyLoopObservation(RoundTrip(Result(mixed.Calls[0], new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle),
            "actor-issued refusal", synthetic: true)));
        var recovered = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        var restored = new TurnStateTracker();
        restored.RestoreCheckpoint(recovered.LoopCheckpoint);

        Assert.Equal(ToolCycleDecisionKind.Stop, restored.EvaluateBeforeDispatch(mixed).Kind);
        Assert.Equal(1, recovered.LoopCheckpoint.Entries.Single(entry => entry.ToolName == "unrelated").EqualRounds);
        Assert.True(recovered.LoopCheckpoint.Entries.Single(entry => entry.ToolName == "probe").Corrected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Queued_new_input_preserves_old_evidence_until_its_durable_adoption(bool snapshot)
    {
        var old = Input("old-input", "old-turn", "operator-a");
        var fresh = Input("new-input", "new-turn", "operator-b");
        var state = SessionState.Empty.Apply(old).Apply(Adoption(old, [old.InputId]));
        state = CompleteTwoRounds(state, "old-turn").Apply(fresh);
        if (snapshot)
            state = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.Equal("old-turn", state.AdoptedTaskContext!.TurnId);
        Assert.Equal("operator-a", state.AdoptedTaskContext.RequesterSenderId!.Value.Value);
        Assert.All(state.LoopCheckpoint.Entries, entry => Assert.Equal(2, entry.EqualRounds));

        state = state.CloseInputs([old.InputId]).Apply(RoundTrip(Adoption(fresh, [fresh.InputId])));
        var after = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.Equal("new-turn", after.LoopCheckpoint.TaskId);
        Assert.Equal("operator-b", after.AdoptedTaskContext!.RequesterSenderId!.Value.Value);
        Assert.Empty(after.LoopCheckpoint.Entries);
        Assert.Null(after.LoopAdmission);
        Assert.Empty(after.LoopObservations);
        Assert.False(after.LoopReceiptFailure);
    }

    [Fact]
    public void A_duplicate_valid_adoption_does_not_reset_an_established_episode()
    {
        var input = Input("input", "original-turn", "operator-a");
        var adopted = Adoption(input, [input.InputId]);
        var state = CompleteTwoRounds(SessionState.Empty.Apply(input).Apply(RoundTrip(adopted)), "original-turn");
        var repeated = state.Apply(RoundTrip(adopted));
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(repeated.LoopCheckpoint);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(Prepare()).Kind);
        Assert.Equal(state.LoopCheckpoint, repeated.LoopCheckpoint);
    }

    [Theory]
    [InlineData("foreign-requester")]
    [InlineData("foreign-turn")]
    [InlineData("foreign-session")]
    [InlineData("skip-first")]
    [InlineData("reverse-order")]
    [InlineData("incompatible-group")]
    public void An_adoption_rejects_noncanonical_authority_or_input_prefix(string fault)
    {
        var first = Input("first", "turn-a", "operator-a");
        var second = Input("second", "turn-b", fault == "incompatible-group" ? "operator-b" : "operator-a");
        var state = SessionState.Empty.Apply(first).Apply(second);
        var evt = Adoption(second, [first.InputId, second.InputId]);
        evt = fault switch
        {
            "foreign-requester" => evt with { TurnContext = evt.TurnContext with { RequesterSenderId = new SenderId("intruder") } },
            "foreign-turn" => evt with { TurnContext = evt.TurnContext with { TurnId = "foreign-turn" } },
            "foreign-session" => evt with { TurnContext = evt.TurnContext with { SessionId = new SessionId("foreign/session") } },
            "skip-first" => Adoption(second, [second.InputId]),
            "reverse-order" => evt with { InputIds = [second.InputId, first.InputId] },
            _ => evt
        };
        Assert.Throws<InvalidDataException>(() => state.Apply(RoundTrip(evt)));
        Assert.Null(state.AdoptedTaskContext);
        Assert.Equal([first.InputId, second.InputId], state.PendingInputs.Select(input => input.InputId));
    }

    [Fact]
    public void Closed_inputs_retain_the_interrupted_task_but_durable_completion_removes_its_restart_context()
    {
        var input = Input("closed-input", "original-turn", "operator-a");
        var state = SessionState.Empty.Apply(input).Apply(RoundTrip(Adoption(input, [input.InputId])));
        state = CompleteTwoRounds(state, "original-turn").CloseInputs([input.InputId]);
        var recovered = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.Empty(recovered.PendingInputs);
        Assert.Equal("original-turn", recovered.AdoptedTaskContext!.TurnId);
        Assert.Equal("operator-a", recovered.AdoptedTaskContext.RequesterSenderId!.Value.Value);
        Assert.All(recovered.LoopCheckpoint.Entries, entry => Assert.Equal(2, entry.EqualRounds));

        recovered = recovered.Apply(RoundTrip(new TurnRecorded
        {
            SessionId = Session,
            UserMessage = input.UserMessage,
            AssistantReply = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "The task stopped with partial results." }
        }));
        var completed = SessionState.FromSnapshot(RoundTrip(recovered.ToSnapshot()));
        Assert.Null(completed.AdoptedTaskContext);
        Assert.Empty(completed.PendingInputs);
        Assert.Equal("original-turn", completed.LoopCheckpoint.TaskId);
    }

    [Fact]
    public void Serialized_eviction_delta_removes_expired_evidence_and_preserves_pinned_decisions_after_snapshot()
    {
        const string task = "original-admitted-turn";
        var tracker = new TurnStateTracker();
        var corrected = ToolCycleSignatureFactory.Prepare([Call("corrected", "pinned", -1)], Executor);
        var established = ToolCycleSignatureFactory.Prepare([Call("established", "pinned", -2)], Executor);
        foreach (var batch in new[] { corrected, corrected, established, established })
            tracker.ObserveCompleted(ToolCycleSignatureFactory.Complete(batch, batch.Calls.ToDictionary(
                call => call.CallId.Value, _ => new ToolCycleResult(ToolInvocationOutcomeCategory.TransientFailure, "same failure"))));
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(corrected).Kind);
        var cold = Enumerable.Range(0, 257).Select(index => ToolCycleSignatureFactory.Prepare(
            [Call($"cold-{index}", "cold", index)], Executor)).ToArray();
        foreach (var batch in cold.Take(256))
            tracker.ObserveCompleted(ToolCycleSignatureFactory.Complete(batch, batch.Calls.ToDictionary(
                call => call.CallId.Value, _ => new ToolCycleResult(ToolInvocationOutcomeCategory.TransientFailure, "same failure"))));
        var previous = tracker.CaptureCheckpoint(task);
        tracker.ObserveCompleted(ToolCycleSignatureFactory.Complete(cold[256], cold[256].Calls.ToDictionary(
            call => call.CallId.Value, _ => new ToolCycleResult(ToolInvocationOutcomeCategory.TransientFailure, "same failure"))));
        var next = Prepare();
        var admitted = new ToolBatchStarted
        {
            SessionId = Session,
            LoopAdmission = new ToolLoopAdmission
            {
                TaskId = task, ActionHash = next.Action.Value,
                Calls = next.Calls.Select(call => new ToolLoopPreparedCall(
                    call.CallId.Value, call.ToolName.Value, call.ArgumentsHash, call.AllowsPendingJob)).ToArray()
            },
            LoopDelta = tracker.CaptureDelta(task, previous)
        };
        Assert.False(admitted.LoopDelta.Reset);
        Assert.Single(admitted.LoopDelta.RemovedKeys);
        var state = (SessionState.Empty with { LoopCheckpoint = previous }).ApplyLoopAdmission(RoundTrip(admitted));
        var recovered = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        var restored = new TurnStateTracker();
        restored.RestoreCheckpoint(recovered.LoopCheckpoint);

        Assert.Equal(ToolCycleDecisionKind.Stop, restored.EvaluateBeforeDispatch(corrected).Kind);
        Assert.Equal(ToolCycleDecisionKind.Correct, restored.EvaluateBeforeDispatch(established).Kind);
        Assert.Equal(ToolCycleDecisionKind.Execute, restored.EvaluateBeforeDispatch(cold[0]).Kind);
        restored.ObserveCompleted(ToolCycleSignatureFactory.Complete(cold[0], cold[0].Calls.ToDictionary(
            call => call.CallId.Value, _ => new ToolCycleResult(ToolInvocationOutcomeCategory.TransientFailure, "same failure"))));
        Assert.Equal(ToolCycleDecisionKind.Execute, restored.EvaluateBeforeDispatch(cold[0]).Kind);
        Assert.Equal(256, recovered.LoopCheckpoint.ColdKeys.Count);
        Assert.Equal(258, recovered.LoopCheckpoint.Entries.Count);
    }

    private SessionState CompleteTwoRounds(SessionState state, string task)
    {
        var batch = Prepare();
        for (var round = 0; round < 2; round++)
        {
            state = Admit(state, batch, task, []);
            foreach (var call in batch.Calls)
                state = state.ApplyLoopObservation(RoundTrip(Result(call, new ToolInvocationReceipt.Succeeded([], null), "same actual result")));
        }
        return state;
    }

    private static InputAdmitted Input(string id, string turn, string requester) => new()
    {
        SessionId = Session, InputId = new InputId(id),
        UserMessage = new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = id },
        TurnContext = TurnContext.FromMessageSource(Session, new TurnId(turn), new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester),
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        }).ToRecord()
    };

    private static ToolTaskAdopted Adoption(InputAdmitted input, IReadOnlyList<InputId> ids) => new(false)
    { SessionId = Session, TurnContext = input.TurnContext, InputIds = ids };

    private SessionState Admit(SessionState state, PreparedToolCycleBatch batch, string taskId, IReadOnlyList<string> refused)
    {
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(state.LoopCheckpoint);
        if (refused.Count != 0)
            Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(batch).Kind);
        var evt = new ToolBatchStarted
        {
            SessionId = Session,
            LoopAdmission = new ToolLoopAdmission
            {
                TaskId = taskId, ActionHash = batch.Action.Value,
                Calls = batch.Calls.Select(call => new ToolLoopPreparedCall(
                    call.CallId.Value, call.ToolName.Value, call.ArgumentsHash, call.AllowsPendingJob)).ToArray(),
                RefusedCallIds = refused
            },
            LoopDelta = tracker.CaptureDelta(taskId, state.LoopCheckpoint)
        };
        return state.ApplyLoopAdmission(RoundTrip(evt));
    }

    private static ToolCallRecorded Result(PreparedToolCycleCall call, ToolInvocationReceipt? receipt, string output, bool synthetic = false)
        => new()
        {
            SessionId = Session,
            ToolResult = new SerializableChatMessage
            {
                Role = Netclaw.Actors.Protocol.ChatRole.Tool,
                Name = call.ToolName.Value, ToolCallId = call.CallId, Content = output
            },
            LoopObservation = ToolCycleSignatureFactory.CreateObservation(call.CallId.Value, receipt, output, synthetic)
        };

    private static PreparedToolCycleBatch Prepare() => ToolCycleSignatureFactory.Prepare(
        [Call("first", "probe", 1), Call("duplicate", "probe", 1), Call("other", "diagnostic", 2)], Executor);

    private static FunctionCallContent Call(string id, string tool, int value) => new(id, tool,
        new Dictionary<string, object?> { ["value"] = value, ["_rationale"] = "Apply the durable suffix case." });

    private T RoundTrip<T>(T value)
    {
        var serializer = Sys.Serialization.FindSerializerFor(value);
        var bytes = serializer.ToBinary(value);
        var manifest = serializer is SerializerWithStringManifest named ? named.Manifest(value) : string.Empty;
        return (T)Sys.Serialization.Deserialize(bytes, serializer.Identifier, manifest);
    }
}
