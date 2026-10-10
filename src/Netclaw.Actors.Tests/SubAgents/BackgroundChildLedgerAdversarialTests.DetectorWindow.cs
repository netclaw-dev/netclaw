// -----------------------------------------------------------------------
// <copyright file="BackgroundChildLedgerAdversarialTests.DetectorWindow.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Protobuf;
using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using StoredRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildLedgerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_child_window_marker_changes_detector_evidence_without_new_authority(bool startsWindow)
    {
        var (state, run, adoption) = WindowDelivery(startsWindow, false);
        Assert.True(BackgroundChildRun.SameCheckpoint(run.ParentCheckpoint, state.LoopCheckpoint));
        var wire = NetclawProtoMapper.ToProto(adoption).ToByteArray();
        var fields = new List<int>();
        using (var reader = new CodedInputStream(wire))
            while (reader.ReadTag() is var tag && tag != 0)
            {
                fields.Add(WireFormat.GetTagFieldNumber(tag));
                reader.SkipLastField();
            }
        Assert.Equal(startsWindow, fields.Contains(7));
        var decoded = RoundTrip(adoption);
        Assert.Equal(startsWindow, decoded.StartsChildContinuationWindow);
        var adopted = SessionState.FromSnapshot(RoundTrip(state.Apply(decoded).ToSnapshot()));
        Assert.True(SessionState.SameCanonicalContext(run.OriginalContext, adopted.AdoptedTaskContext!));
        Assert.Equal(run.ParentCheckpoint.TaskId, adopted.LoopCheckpoint.TaskId);
        Assert.NotEqual(run.OriginalContext.TurnId, adopted.LoopCheckpoint.TaskId);
        Assert.Equal(adoption.InputIds, adopted.AdoptedTaskInputIds);
        Assert.Equal(ChildRunDelivery.Body(run), Assert.Single(adopted.PendingInputs).UserMessage.Content);
        if (!startsWindow)
        {
            Assert.True(BackgroundChildRun.SameCheckpoint(run.ParentCheckpoint, adopted.LoopCheckpoint));
            return;
        }
        Assert.Empty(adopted.LoopCheckpoint.Entries);
        Assert.Empty(adopted.LoopCheckpoint.ColdKeys);
        Assert.Empty(adopted.LoopCheckpoint.AdjacentHistory);
        Assert.Null(adopted.LoopCheckpoint.LastBlockedAction);
        Assert.True(BackgroundChildRun.SameCheckpoint(adopted.LoopCheckpoint, adopted.ChildRuns[run.RunId].ParentCheckpoint));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_new_child_window_keeps_every_existing_missing_receipt_failure(bool retained, bool current)
    {
        var (state, _, adoption) = WindowDelivery(true, retained);
        state = state with { LoopReceiptFailure = current };
        var adopted = SessionState.FromSnapshot(RoundTrip(state.Apply(RoundTrip(adoption)).ToSnapshot()));
        Assert.True(adopted.LoopReceiptFailure);
        Assert.All(adopted.ChildRuns.Values, child => Assert.True(child.ParentReceiptFailure));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Duplicate_child_adoption_keeps_new_evidence_after_consumption_and_compaction(bool consume, bool compact)
    {
        var (state, _, adoption) = WindowDelivery(true, false);
        state = state.Apply(RoundTrip(adoption));
        state = ObserveWindowProbe(state, "new-window-1");
        state = ObserveWindowProbe(state, "new-window-2");
        if (consume)
            state = state.CloseInputs(adoption.InputIds);
        if (compact)
            state = state.Apply(RoundTrip(new SessionCompacted
            {
                SessionId = Owner, Summary = "The child result remains in durable session state.",
                CompactedMessages = [new SerializableChatMessage { Role = StoredRole.User, Content = "The retained task summary." }]
            }));
        state = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        var before = Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot());
        var duplicate = state.Apply(RoundTrip(adoption));
        Assert.Equal(before, Codec(duplicate.ToSnapshot()).ToBinary(duplicate.ToSnapshot()));
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(duplicate.LoopCheckpoint);
        Assert.Equal(ToolCycleDecisionKind.Correct, tracker.EvaluateBeforeDispatch(WindowProbe("next-window-3")).Kind);
        RejectUnchanged(state, () => state.Apply(adoption with { TurnContext = adoption.TurnContext with { RequesterSenderId = new SenderId("other") } }));
        RejectUnchanged(state, () => state.Apply(adoption with { InputIds = [new InputId("other-result")] }));
    }

    [Fact]
    public void An_unrelated_adoption_cannot_request_a_child_window()
    {
        var (state, run) = Acceptance(Owner, slash: true);
        RejectUnchanged(state, () => state.Apply(new ToolTaskAdopted(true)
        { SessionId = Owner, TurnContext = run.OriginalContext, InputIds = run.OriginInputIds }));
        Assert.Equal(state, state.Apply(new ToolTaskAdopted(false)
        { SessionId = Owner, TurnContext = run.OriginalContext, InputIds = run.OriginInputIds }));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("arguments")]
    [InlineData("body")]
    [InlineData("result-name")]
    [InlineData("orphan-result")]
    [InlineData("assistant-role")]
    public void A_duplicate_after_consumption_rejects_corruption_of_its_retained_attributed_pair(string fault)
    {
        var (state, _, adoption) = WindowDelivery(true, false);
        state = state.Apply(RoundTrip(adoption)).CloseInputs(adoption.InputIds);
        var call = Assert.Single(state.History, message => message.ToolCalls.Any(item => item.CallId.Value == "window-framework-result"));
        var result = Assert.Single(state.History, message => message.ToolCallId?.Value == "window-framework-result");
        Assert.Equal(state, state.Apply(RoundTrip(adoption)));
        var changed = fault switch
        {
            "name" => call with { ToolCalls = [call.ToolCalls[0] with { Name = new ToolName("shell") }] },
            "arguments" => call with { ToolCalls = [call.ToolCalls[0] with { ArgumentsJson = "{}" }] },
            "body" => result with { Content = "The result replaces the actual durable outcome." },
            "result-name" => result with { Name = "unrelated_tool" },
            "assistant-role" => call with { Role = StoredRole.User },
            "orphan-result" => call,
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        state = state with
        {
            History = fault == "orphan-result" ? state.History.Remove(call)
                : state.History.Select(message => ReferenceEquals(message, fault is "body" or "result-name" ? result : call) ? changed : message).ToImmutableList()
        };
        RejectUnchanged(state, () => state.Apply(RoundTrip(adoption)));
    }

    [Fact]
    public void An_unrelated_tool_pair_with_the_same_arguments_and_body_cannot_replace_delivery_identity()
    {
        var (state, run, adoption) = WindowDelivery(true, false);
        state = state.Apply(RoundTrip(adoption)).CloseInputs(adoption.InputIds);
        var call = ChildRunDelivery.Call(run, new ToolCallId("unrelated-call"));
        call = call with { ToolCalls = [call.ToolCalls[0] with { Name = new ToolName("unrelated_tool") }] };
        state = state with { History = state.History.Add(call).Add(new SerializableChatMessage
        { Role = StoredRole.Tool, ToolCallId = new ToolCallId("unrelated-call"), Name = "unrelated_tool", Content = ChildRunDelivery.Body(run) }) };
        Assert.Equal(state, state.Apply(RoundTrip(adoption)));
    }

    [Fact]
    public void A_canonical_shell_job_continuation_cannot_request_a_child_window()
    {
        var (_, run) = Acceptance(Owner, slash: true);
        const string key = "bg-job:canonical-job";
        var origin = new BackgroundJobOrigin(new TurnId(run.ParentCheckpoint.TaskId), new ToolCallId("job-start"));
        var job = new ActiveJobInfo
        {
            JobId = new BackgroundJobId("canonical-job"), Command = "echo result", Rationale = "Read the assigned job result.",
            StartedAtMs = 0, Audience = run.OriginalContext.Audience, Boundary = run.OriginalContext.Boundary!.Value,
            LineageVersion = 1, Origin = origin, OriginCheckpoint = run.ParentCheckpoint
        };
        var input = new InputAdmitted
        {
            SessionId = Owner, InputId = new InputId("job-result"), SourceMessageId = key,
            SourceBackgroundJobId = new BackgroundJobId(key), BackgroundJobLineageVersion = 1, BackgroundJobOrigin = origin,
            TurnContext = run.OriginalContext with
            {
                TurnId = key, SourceKind = BackgroundJobManagerActor.SourceKind,
                RequesterPrincipal = PrincipalClassification.VerifiedAutomation, TransportAuthenticity = TransportAuthenticity.LocalProcess
            },
            UserMessage = new SerializableChatMessage { Role = StoredRole.User, Content = "The job returned its result." }
        };
        var state = SessionState.Empty.TrackBackgroundJob(key, job).Apply(input);
        Assert.True(state.TryGetBackgroundContinuation(input, out _, out _));
        var ordinary = new ToolTaskAdopted(false)
        { SessionId = Owner, TurnContext = input.TurnContext, InputIds = [input.InputId], ContinuedJobKey = key };
        Assert.True(BackgroundChildRun.SameCheckpoint(run.ParentCheckpoint, state.Apply(RoundTrip(ordinary)).LoopCheckpoint));
        RejectUnchanged(state, () => state.Apply(RoundTrip(ordinary with { StartsChildContinuationWindow = true })));
    }

    [Fact]
    public void A_later_sibling_uses_current_evidence_and_starts_its_own_first_window()
    {
        var (state, first, firstAdoption) = WindowDelivery(true, false);
        var second = first with
        {
            RunId = new SubAgentRunId("later-sibling"), ScopeId = new SubAgentScopeId($"{Owner.Value}/subagent/worker/later-sibling"),
            StartKey = new ChildRunStartKey.Slash(new InputId("origin-two")) { SessionId = Owner, TurnId = new TurnId(first.OriginalContext.TurnId) },
            Terminal = null, PreparedTerminal = null, TerminalSequenceNr = null, DeliveryInputId = null
        };
        var job = new ActiveJobInfo
        {
            JobId = new BackgroundJobId("same-parent-job"), Command = "echo result", Rationale = "Read the assigned result.",
            StartedAtMs = 0, Audience = first.OriginalContext.Audience, Boundary = first.OriginalContext.Boundary!.Value,
            LineageVersion = 1, Origin = new BackgroundJobOrigin(new TurnId(first.ParentCheckpoint.TaskId), new ToolCallId("job-start")),
            OriginCheckpoint = first.ParentCheckpoint
        };
        state = state.TrackBackgroundJob("bg-job:same-parent-job", job);
        state = state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Owner, Run = second }));
        state = state.Apply(RoundTrip(firstAdoption));
        Assert.Empty(state.ChildRuns[second.RunId].ParentCheckpoint.Entries);
        Assert.True(BackgroundChildRun.SameCheckpoint(state.LoopCheckpoint,
            state.ActiveBackgroundJobs["bg-job:same-parent-job"].OriginCheckpoint!));
        state = ObserveWindowProbe(state, "between-siblings-1");
        state = ObserveWindowProbe(state, "between-siblings-2");
        state = state.CloseInputs(firstAdoption.InputIds);
        second = state.ChildRuns[second.RunId];
        Assert.Equal(2, Assert.Single(second.ParentCheckpoint.Entries).EqualRounds);
        Assert.Equal(2, Assert.Single(state.ActiveBackgroundJobs["bg-job:same-parent-job"].OriginCheckpoint!.Entries).EqualRounds);
        var terminal = Terminal(second);
        state = state.Apply(RoundTrip(new ChildRunEvent.TerminalRecorded(terminal, 2)
        { SessionId = Owner, RunId = second.RunId, RecordedAtMs = 123461 }));
        state = state.Apply(RoundTrip(new ChildRunEvent.ResultPrepared(terminal, 2)
        { SessionId = Owner, RunId = second.RunId, RecordedAtMs = 123462 }));
        second = state.ChildRuns[second.RunId];
        var input = Delivery(second, "second-framework-result");
        state = state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(input)
        { SessionId = Owner, RunId = second.RunId, RecordedAtMs = 123463 }));
        Assert.Equal(2, Assert.Single(state.LoopCheckpoint.Entries).EqualRounds);
        var next = state.Apply(RoundTrip(new ToolTaskAdopted(true)
        { SessionId = Owner, TurnContext = second.OriginalContext, InputIds = [input.InputId], ContinuedChildRunId = second.RunId }));
        Assert.Empty(next.LoopCheckpoint.Entries);
        Assert.Empty(next.ActiveBackgroundJobs["bg-job:same-parent-job"].OriginCheckpoint!.Entries);
        Assert.Equal(first.ParentCheckpoint.TaskId, next.LoopCheckpoint.TaskId);
        Assert.True(SessionState.SameCanonicalContext(first.OriginalContext, next.AdoptedTaskContext!));
    }

    [Fact]
    public void Only_the_committed_adoption_changes_the_retained_detector_window()
    {
        var (state, run) = AcceptedSlash(false);
        var retained = state.LoopCheckpoint;
        var terminal = Terminal(run);
        var recorded = new ChildRunEvent.TerminalRecorded(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123458 };
        var prepared = new ChildRunEvent.ResultPrepared(terminal, 1)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123459 };
        state = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.True(BackgroundChildRun.SameCheckpoint(retained, state.LoopCheckpoint));
        state = SessionState.FromSnapshot(RoundTrip(state.Apply(RoundTrip(recorded)).ToSnapshot()));
        Assert.True(BackgroundChildRun.SameCheckpoint(retained, state.LoopCheckpoint));
        state = SessionState.FromSnapshot(RoundTrip(state.Apply(RoundTrip(prepared)).ToSnapshot()));
        Assert.True(BackgroundChildRun.SameCheckpoint(retained, state.LoopCheckpoint));
        run = state.ChildRuns[run.RunId];
        var input = Delivery(run, "committed-window-result");
        state = SessionState.FromSnapshot(RoundTrip(state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(input)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 })).ToSnapshot()));
        Assert.True(BackgroundChildRun.SameCheckpoint(retained, state.LoopCheckpoint));
        var adoption = new ToolTaskAdopted(true)
        { SessionId = Owner, TurnContext = run.OriginalContext, InputIds = [input.InputId], ContinuedChildRunId = run.RunId };
        var adopted = SessionState.FromSnapshot(RoundTrip(state.Apply(RoundTrip(adoption)).ToSnapshot()));
        Assert.Empty(adopted.LoopCheckpoint.Entries);
        Assert.Equal(retained.TaskId, adopted.LoopCheckpoint.TaskId);
        Assert.True(SessionState.SameCanonicalContext(run.OriginalContext, adopted.AdoptedTaskContext!));
    }

    private (SessionState State, BackgroundChildRun Run, ToolTaskAdopted Adoption) WindowDelivery(bool marker, bool missing)
    {
        var (state, run) = PreparedSlash(missing);
        var input = Delivery(run, "window-framework-result");
        state = state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(input)
        { SessionId = Owner, RunId = run.RunId, RecordedAtMs = 123460 }));
        return (state, run, new ToolTaskAdopted(marker)
        { SessionId = Owner, TurnContext = run.OriginalContext, InputIds = [input.InputId], ContinuedChildRunId = run.RunId });
    }

    private PreparedToolCycleBatch WindowProbe(string id) => ToolCycleSignatureFactory.Prepare(
        [new FunctionCallContent(id, "window_probe", new Dictionary<string, object?>
        { ["value"] = "neutral", ["_rationale"] = "Read the unchanged neutral value." })], _tools);

    private SessionState ObserveWindowProbe(SessionState state, string id)
    {
        var tracker = new TurnStateTracker();
        tracker.RestoreCheckpoint(state.LoopCheckpoint);
        var batch = WindowProbe(id);
        Assert.Equal(ToolCycleDecisionKind.Execute, tracker.EvaluateBeforeDispatch(batch).Kind);
        state = state.ApplyLoopAdmission(RoundTrip(new ToolBatchStarted
        {
            SessionId = Owner, LoopDelta = tracker.CaptureDelta(state.LoopCheckpoint.TaskId, state.LoopCheckpoint),
            LoopAdmission = new ToolLoopAdmission
            {
                TaskId = state.LoopCheckpoint.TaskId, ActionHash = batch.Action.Value,
                Calls = batch.Calls.Select(call => new ToolLoopPreparedCall(call.CallId.Value, call.ToolName.Value,
                    call.ArgumentsHash, call.AllowsPendingJob)).ToArray()
            }
        }));
        return state.ApplyLoopObservation(RoundTrip(new ToolCallRecorded
        {
            SessionId = Owner, LoopObservation = ToolCycleSignatureFactory.CreateObservation(id,
                new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure), "The exact neutral outcome.", false)
        }));
    }
}
