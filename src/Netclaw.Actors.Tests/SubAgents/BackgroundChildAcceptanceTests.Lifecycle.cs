// -----------------------------------------------------------------------
// <copyright file="BackgroundChildAcceptanceTests.Lifecycle.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildAcceptanceTests
{
    [Fact]
    public void Atomic_child_delivery_restores_the_original_task_after_a_fresh_completed_turn()
    {
        var state = AdmitBatch(AdmittedTask("original", SessionState.Empty), "start");
        var accepted = Run(state, "run-delivery", "start");
        state = state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Session, Run = accepted }));
        state = Observe(state, "start", missingReceipt: false);
        var terminal = new ChildRunTerminal(new SubAgentResult
        {
            Completion = new ChildRunCompletion.Completed(new WorkingContextDelta { ReadFiles = ["/project/read.txt"] }),
            Output = "The child confirmed its result.", AgentName = accepted.AgentName,
            RunId = accepted.RunId, ScopeId = accepted.ScopeId
        }, false, null);
        state = state.Apply(RoundTrip(new ChildRunEvent.TerminalRecorded(terminal, 1)
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 1
        }));
        state = state.Apply(RoundTrip(new ChildRunEvent.ResultPrepared(terminal, 1)
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 2
        }));
        state = state.Apply(new TurnRecorded { SessionId = Session });
        state = AdmittedTask("fresh", state);
        state = state.CloseInputs(state.AdoptedTaskInputIds).Apply(new TurnRecorded { SessionId = Session });
        state = state with { WorkingContext = new WorkingContext { ProjectDirectory = "/new-parent-project" } };
        var retained = state.ChildRuns[accepted.RunId];
        var input = new InputAdmitted
        {
            SessionId = Session, InputId = new InputId($"child-result-{accepted.RunId.Value}"),
            SourceChildRunId = accepted.RunId, TurnContext = accepted.OriginalContext,
            UserMessage = new SerializableChatMessage
            {
                Role = ChatRole.Tool, Name = "spawn_agent", ToolCallId = new ToolCallId("fresh-framework-result"),
                Content = ChildRunDelivery.Body(retained)
            }
        };
        state = state.Apply(RoundTrip(new ChildRunEvent.DeliveryAdmitted(input)
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 3
        }));
        state = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        var pending = Assert.Single(state.PendingInputs);
        Assert.Equal(accepted.RunId, pending.SourceChildRunId);
        Assert.Equal(pending.InputId, state.ChildRuns[accepted.RunId].DeliveryInputId);
        state = state.Apply(RoundTrip(new ToolTaskAdopted
        {
            SessionId = Session, TurnContext = accepted.OriginalContext, InputIds = [pending.InputId],
            ContinuedChildRunId = accepted.RunId
        }));
        Assert.Equal("original", state.LoopCheckpoint.TaskId);
        Assert.Equal(1, Assert.Single(state.LoopCheckpoint.Entries).EqualRounds);
        Assert.Equal("/new-parent-project", state.WorkingContext.ProjectDirectory);
        Assert.False(state.LoopReceiptFailure);
        Assert.Equal(accepted.OriginalContext, state.AdoptedTaskContext);
        Assert.Throws<InvalidDataException>(() => state.Apply(new ChildRunEvent.DeliveryAdmitted(input)
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 4
        }));
    }

    [Fact]
    public void Coalesced_child_adoption_retains_both_pairs_after_input_closure_and_snapshot()
    {
        var state = AdmitBatch(AdmittedTask("original", SessionState.Empty), "first", "second");
        var runs = new[] { Run(state, "first-run", "first"), Run(state, "second-run", "second") };
        foreach (var run in runs)
            state = state.Apply(new ChildRunAccepted { SessionId = Session, Run = run });
        state = Observe(Observe(state, "first", false), "second", false).Apply(new TurnRecorded { SessionId = Session });
        var ids = new List<InputId>();
        foreach (var (run, index) in runs.Select((run, index) => (run, index)))
        {
            var terminal = new ChildRunTerminal(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty), Output = run.RunId.Value,
                AgentName = run.AgentName, RunId = run.RunId, ScopeId = run.ScopeId
            }, false, null);
            state = state.Apply(new ChildRunEvent.TerminalRecorded(terminal, index + 1)
            { SessionId = Session, RunId = run.RunId, RecordedAtMs = index + 1 });
            state = state.Apply(new ChildRunEvent.ResultPrepared(terminal, index + 1)
            { SessionId = Session, RunId = run.RunId, RecordedAtMs = index + 1 });
            var input = new InputAdmitted
            {
                SessionId = Session, InputId = new InputId($"child-result-{run.RunId.Value}"),
                SourceChildRunId = run.RunId, TurnContext = run.OriginalContext,
                UserMessage = new SerializableChatMessage
                {
                    Role = ChatRole.Tool, Name = ChildRunDelivery.ToolName(run),
                    ToolCallId = new ToolCallId($"result-{index}"), Content = ChildRunDelivery.Body(state.ChildRuns[run.RunId])
                }
            };
            state = state.Apply(new ChildRunEvent.DeliveryAdmitted(input) { SessionId = Session, RunId = run.RunId, RecordedAtMs = index + 1 });
            ids.Add(input.InputId);
        }
        var adoption = RoundTrip(new ToolTaskAdopted
        {
            SessionId = Session, TurnContext = runs[^1].OriginalContext,
            InputIds = ids.ToArray(), ContinuedChildRunId = runs[^1].RunId
        });
        var retainedCount = state.History.Count;
        state = state.Apply(adoption);
        Assert.Equal(retainedCount + 4, state.History.Count);
        state = state.Apply(adoption);
        Assert.Equal(retainedCount + 4, state.History.Count);
        var changed = state with { History = state.History.SetItem(retainedCount + 1,
            state.History[retainedCount + 1] with { Content = "Altered result." }) };
        Assert.Throws<InvalidDataException>(() => changed.Apply(adoption));
        changed = state with { History = state.History.SetItem(retainedCount, state.History[retainedCount] with
        { ToolCalls = [state.History[retainedCount].ToolCalls[0] with { Name = new ToolName("changed") }] }) };
        Assert.Throws<InvalidDataException>(() => changed.Apply(adoption));
        state = state.Apply(new InputClosed { SessionId = Session, InputIds = ids.ToArray(), TaskId = "original" });
        state = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.Empty(state.PendingInputs);
        Assert.Equal(new[] { "result-0", "result-1" }, state.History.Where(message => message.Role == ChatRole.Tool)
            .Select(message => message.ToolCallId!.Value.Value));
        Assert.Equal("original", state.LoopCheckpoint.TaskId);
        Assert.False(state.LoopReceiptFailure);
    }

    [Fact]
    public void Durable_cancel_requires_dispatch_closure_and_preserves_the_cancelled_outcome()
    {
        var state = AdmitBatch(AdmittedTask("cancel", SessionState.Empty), "start");
        var accepted = Run(state, "run-cancel", "start");
        state = state.Apply(new ChildRunAccepted { SessionId = Session, Run = accepted });
        state = state.Apply(RoundTrip(new ChildRunEvent.Started
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 1
        }));
        state = state.Apply(RoundTrip(new ChildRunEvent.Checkpointed(
            new ChildRunCheckpoint(1, "One confirmed read.", new WorkingContextDelta { ReadFiles = ["/project/read.txt"] }))
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 2
        }));
        state = state.Apply(RoundTrip(new ChildRunEvent.CancellationRequested
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 3
        }));
        var terminal = new ChildRunTerminal(new SubAgentResult
        {
            Completion = new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent),
            Output = "The run stopped. External effects remain unconfirmed.", AgentName = accepted.AgentName,
            RunId = accepted.RunId, ScopeId = accepted.ScopeId
        }, false, "The report write failed. The committed checkpoint remains available.");
        var receipt = new ChildRunEvent.TerminalRecorded(terminal, 1)
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 5
        };
        Assert.Throws<InvalidDataException>(() => state.Apply(receipt));
        state = state.Apply(RoundTrip(new ChildRunEvent.DispatchClosed
        {
            SessionId = Session, RunId = accepted.RunId, RecordedAtMs = 4
        }));
        state = state.Apply(RoundTrip(receipt));
        var restored = Assert.Single(SessionState.FromSnapshot(RoundTrip(state.ToSnapshot())).ChildRuns).Value;
        Assert.Equal(BackgroundChildState.Cancelled, restored.State);
        Assert.False(restored.Terminal!.Result.Success);
        Assert.Equal(SubAgentRunOutcome.Failed, restored.Terminal.Result.Outcome);
        Assert.Equal(SubAgentOutcomeReason.CancelledByParent, restored.Terminal.Result.OutcomeReason);
        Assert.Equal("/project/read.txt", Assert.Single(restored.ChildCheckpoint!.ConfirmedActivity.ReadFiles));
        Assert.Equal(terminal.EvidenceWarning, restored.Terminal.EvidenceWarning);
    }
}
