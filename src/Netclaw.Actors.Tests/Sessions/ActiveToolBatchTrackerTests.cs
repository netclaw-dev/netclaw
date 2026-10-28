// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Protobuf;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using ChatMessageProto = Netclaw.Actors.Serialization.Proto.SerializableChatMessageProto;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Media;
using Netclaw.Tools;
using Xunit;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ActiveToolBatchTrackerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Batch_waits_for_both_results_and_the_execution_task(bool reverseResults, bool taskCompletesFirst)
    {
        // Arrange
        var batch = new ActiveToolBatchTracker();
        var turn = new TurnStateTracker();
        batch.Start(Calls(), preparedCycleBatch: null);
        var first = Result(reverseResults ? "call-B" : "call-A");
        var second = Result(reverseResults ? "call-A" : "call-B");
        var state = SessionState.Empty;

        // Act and assert
        if (taskCompletesFirst)
            batch.MarkExecutionTaskCompleted();
        state = batch.ApplyRecordedResult(state, first);
        Assert.IsType<BatchCompletion.Pending>(batch.CompleteIfReady(state, turn));

        state = batch.ApplyRecordedResult(state, second);
        if (!taskCompletesFirst)
        {
            Assert.IsType<BatchCompletion.Pending>(batch.CompleteIfReady(state, turn));
            batch.MarkExecutionTaskCompleted();
        }

        var ready = Assert.IsType<BatchCompletion.Ready>(batch.CompleteIfReady(state, turn));
        Assert.Same(state, ready.State);
        Assert.Equal(2, ready.ResultCount);
        Assert.Equal(1, turn.ToolIterationCount);
        Assert.Equal(2, turn.ToolCallCount);
    }

    [Fact]
    public void Replay_and_duplicate_result_do_not_append_history_or_media_twice()
    {
        // Arrange
        var media = Media("image.png");
        var result = Result("call-A") with { MediaReferences = [media] };
        var batch = new ActiveToolBatchTracker();
        batch.Start([new FunctionCallContent("call-A", "file_read")], preparedCycleBatch: null);

        // Act
        var state = batch.ApplyRecordedResult(SessionState.Empty, result);
        var duplicate = batch.ApplyRecordedResult(state, result);
        var assistant = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls = [new SerializableToolCall { CallId = new ToolCallId("call-A"), Name = new ToolName("file_read") }]
        };
        batch.Clear();
        batch.Start(assistant, [result]);
        var replay = batch.ApplyRecordedResult(duplicate, result);
        batch.MarkExecutionTaskCompleted();
        var completed = Assert.IsType<BatchCompletion.Ready>(
            batch.CompleteIfReady(replay, new TurnStateTracker()));

        // Assert
        Assert.Equal(2, state.History.Count);
        Assert.Single(state.History, message => message.Role == ChatRole.Tool);
        Assert.Equal("image.png", Assert.Single(state.History[^1].MediaReferences).RelativePath);
        Assert.Same(state, duplicate);
        Assert.Same(duplicate, replay);
        Assert.Same(replay, completed.State);
        Assert.Equal(2, completed.State.History.Count);
    }

    [Fact]
    public void Media_nudge_keeps_its_copy_after_batch_reset()
    {
        // Arrange
        var media = Media("source.png");
        var references = new List<SerializableMediaReference> { media };
        var batch = new ActiveToolBatchTracker();
        batch.Start([new FunctionCallContent("call-A", "file_read")], preparedCycleBatch: null);

        // Act
        var state = batch.ApplyRecordedResult(SessionState.Empty, Result("call-A") with { MediaReferences = references });
        references.Clear();
        batch.DiscardPendingMedia();
        batch.Clear();

        // Assert
        Assert.Equal("source.png", Assert.Single(state.History[^1].MediaReferences).RelativePath);
        Assert.Contains("media file", state.History[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Discarded_pending_media_does_not_reach_a_later_result()
    {
        // Arrange
        var batch = new ActiveToolBatchTracker();
        batch.Start(Calls(), preparedCycleBatch: null);
        var first = Result("call-A") with { MediaReferences = [Media("discard.png")] };

        // Act
        var state = batch.ApplyRecordedResult(SessionState.Empty, first);
        batch.DiscardPendingMedia();
        state = batch.ApplyRecordedResult(state, Result("call-B"));

        // Assert
        Assert.Equal(2, state.History.Count);
        Assert.DoesNotContain(state.History, message => message.Content.Contains("media file", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ready_batch_compacts_only_when_compaction_is_due(bool compactionDue)
    {
        // Arrange
        var ready = new BatchCompletion.Ready(SessionState.Empty, 1);
        var expectedAction = compactionDue
            ? ToolBatchNextAction.RequestCompaction
            : ToolBatchNextAction.ContinueModel;

        // Act
        var action = ready.ChooseNext(compactionDue);

        // Assert
        Assert.Equal(expectedAction, action);
    }

    [Fact]
    public void Completed_outcomes_replace_progress_nudges_and_a_success_clears_them()
    {
        var batch = new ActiveToolBatchTracker();
        var turn = new TurnStateTracker();
        var state = SessionState.Empty;
        for (var i = 0; i < 8; i++)
        {
            var call = new FunctionCallContent($"call-{i}", $"sample/{i}");
            Assert.NotEqual(RefusedRepeatAction.EndTurn, turn.EvaluateProgress());
            batch.Start([call], new PreparedToolCycleBatch(new ToolActionSignature($"action-{i}"),
                [new PreparedToolCycleCall(new ToolCallId(call.CallId), new ToolName(call.Name), $"args-{i}")]));
            var message = Result(call.CallId);
            var receipt = i < 7 ? (ToolInvocationReceipt)new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput)
                : new ToolInvocationReceipt.Succeeded([], null);
            batch.RecordOutcome(new ToolCallResult(message, [], [], [], AuthorizationAttemptId.New(), Receipt: receipt));
            state = batch.ApplyRecordedResult(state, message);
            batch.MarkExecutionTaskCompleted();
            var ready = Assert.IsType<BatchCompletion.Ready>(batch.CompleteIfReady(state, turn));
            state = ready.State;
            var nudges = state.History.Where(message => ToolCycleMessages.IsLoopNudge(message.Content)).ToArray();
            if (i is >= 3 and < 7)
            {
                var nudge = Assert.Single(nudges);
                Assert.Same(nudge, state.History[^1]);
                Assert.Contains($"{i + 1} tool calls failed", nudge.Content, StringComparison.Ordinal);
                if (i >= 5)
                    Assert.Contains("End your turn now", nudge.Content, StringComparison.Ordinal);
            }
            else
                Assert.Empty(nudges);
            Assert.IsType<BatchCompletion.Pending>(batch.CompleteIfReady(state, turn));
            Assert.Equal(i + 1, turn.ToolIterationCount);
        }
        Assert.Equal(0, turn.CallsSinceProgress);
    }

    [Fact]
    public void Approval_redrive_outcomes_update_progress_without_cycle_history()
    {
        var batch = new ActiveToolBatchTracker();
        var turn = new TurnStateTracker();
        turn.ObserveProgress([new(ToolInvocationOutcomeCategory.InvalidInput, "Error.")], batchComplete: true);
        var call = new FunctionCallContent("call-approved", "sample/a");
        batch.Start([call], preparedCycleBatch: null);
        var message = Result(call.CallId);
        batch.RecordOutcome(new ToolCallResult(message, [], [], [], AuthorizationAttemptId.New(),
            Receipt: new ToolInvocationReceipt.Succeeded([], null)));
        var state = batch.ApplyRecordedResult(SessionState.Empty, message);
        batch.MarkExecutionTaskCompleted();

        Assert.IsType<BatchCompletion.Ready>(batch.CompleteIfReady(state, turn));
        Assert.Equal(0, turn.CallsSinceProgress);
        Assert.Equal(0, turn.CompletedCycleHistoryCount);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("success")]
    [InlineData("pending")]
    [InlineData("synthetic")]
    public void Recovered_parent_approval_keeps_all_sibling_outcomes_once(string siblingOutcome)
    {
        var workflow = new SessionToolWorkflow();
        var turn = new TurnStateTracker();
        var successfulSibling = siblingOutcome == "success";
        if (successfulSibling)
            turn.ObserveProgress(Enumerable.Repeat(new ToolCycleResult(ToolInvocationOutcomeCategory.InvalidInput, "Earlier failure."), 3), true);
        var calls = Enumerable.Range(0, 4).Select(i => new FunctionCallContent($"call-{i}", $"sample/{i}")).ToArray();
        var started = new SessionProtocol.ToolBatchStarted
        {
            SessionId = new SessionId("test/approval-progress"),
            AssistantMessage = new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = calls.Select(call => new SerializableToolCall
                {
                    CallId = new ToolCallId(call.CallId), Name = new ToolName(call.Name)
                }).ToArray()
            },
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Inspect the files." }
        };
        var state = workflow.ApplyBatchStarted(SessionState.Empty, started);
        workflow.StartDispatch(calls, cycle: null, executor: null, recordCompletedCycle: true);
        for (var i = 0; i < 3; i++)
        {
            ToolInvocationReceipt receipt = i == 0 ? siblingOutcome switch
            {
                "success" => new ToolInvocationReceipt.Succeeded([], null),
                "pending" => new ToolInvocationReceipt.PendingBackgroundJob(),
                "synthetic" => new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle),
                _ => new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput)
            } : new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput);
            var outcome = new ToolCallResult(Result(calls[i].CallId), [], [], [], AuthorizationAttemptId.New(), Receipt: receipt);
            workflow.RecordOutcome(outcome);
            state = workflow.ApplyRecordedResult(state, Wire(outcome.MessageWithProgress));
        }
        workflow.MarkExecutionTaskCompleted();
        Assert.IsType<BatchCompletion.Pending>(workflow.CompleteIfReady(state, turn));

        // A fresh workflow reads the journal facts, as it does after a restart during approval.
        workflow = new SessionToolWorkflow();
        state = workflow.ApplyBatchStarted(state, started);
        workflow.StartDispatch([calls[3]], cycle: null, executor: null, recordCompletedCycle: false);
        var final = new ToolCallResult(Result(calls[3].CallId), [], [], [], AuthorizationAttemptId.New(),
            Receipt: new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
        workflow.RecordOutcome(final);
        workflow.RecordOutcome(final);
        state = workflow.ApplyRecordedResult(state, Wire(final.MessageWithProgress));
        workflow.MarkExecutionTaskCompleted();
        var ready = Assert.IsType<BatchCompletion.Ready>(workflow.CompleteIfReady(state, turn));
        Assert.Equal(4, ready.ResultCount);
        Assert.Equal(successfulSibling ? 0 : siblingOutcome == "failed" ? 4 : 3, turn.CallsSinceProgress);
        Assert.Equal(siblingOutcome == "failed" ? RefusedRepeatAction.Nudge : RefusedRepeatAction.None, turn.EvaluateProgress());
        Assert.Equal(0, turn.CompletedCycleHistoryCount);
        Assert.IsType<BatchCompletion.Pending>(workflow.CompleteIfReady(ready.State, turn));
        Assert.Equal(1, turn.ToolIterationCount);

        static SerializableChatMessage Wire(SerializableChatMessage message)
            => NetclawProtoMapper.FromProto(ChatMessageProto.Parser.ParseFrom(
                NetclawProtoMapper.ToProto(message).ToByteArray()));
    }

    [Theory]
    [InlineData("invalid_rationale")]
    [InlineData("unrecognized_argument")]
    [InlineData("args_parse_error")]
    [InlineData("invalid_meta_value")]
    public void Front_door_rejection_flows_its_failure_key_into_the_cycle_result(string code)
    {
        // Arrange
        var batch = new ActiveToolBatchTracker();
        batch.Start([new FunctionCallContent("call-A", "file_read")], preparedCycleBatch: null);
        var result = new ToolCallResult(Result("call-A"), [], [], [], AuthorizationAttemptId.New(),
            FailureCode: code,
            Receipt: new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));

        // Act
        var callId = batch.RecordOutcome(result);

        // Assert
        Assert.Equal(new ToolFailureKey("netclaw", code), batch.CycleResult(callId)?.FailureKey);
    }

    [Fact]
    public void Access_denied_flows_no_failure_key()
    {
        // Arrange
        var batch = new ActiveToolBatchTracker();
        batch.Start([new FunctionCallContent("call-A", "file_read")], preparedCycleBatch: null);
        var result = new ToolCallResult(Result("call-A"), [], [], [], AuthorizationAttemptId.New(),
            FailureCode: SessionProtocol.ToolResultOutput.AccessDeniedFailureCode,
            Receipt: new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.AccessDenied));

        // Act
        var callId = batch.RecordOutcome(result);

        // Assert
        Assert.Null(batch.CycleResult(callId)?.FailureKey);
    }

    [Fact]
    public void A_null_failure_code_flows_no_failure_key()
    {
        // Arrange
        var batch = new ActiveToolBatchTracker();
        batch.Start([new FunctionCallContent("call-A", "file_read")], preparedCycleBatch: null);
        var result = new ToolCallResult(Result("call-A"), [], [], [], AuthorizationAttemptId.New(),
            Receipt: new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure));

        // Act
        var callId = batch.RecordOutcome(result);

        // Assert
        Assert.Null(batch.CycleResult(callId)?.FailureKey);
    }

    [Fact]
    public void Failure_key_survives_the_journal_round_trip_and_restore()
    {
        // Arrange
        var live = new ActiveToolBatchTracker();
        live.Start([new FunctionCallContent("call-A", "file_read")], preparedCycleBatch: null);
        var result = new ToolCallResult(Result("call-A"), [], [], [], AuthorizationAttemptId.New(),
            FailureCode: "invalid_rationale",
            Receipt: new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
        var callId = live.RecordOutcome(result);
        var journaled = Wire(result.MessageWithProgress);

        // Act
        var restored = new ActiveToolBatchTracker();
        restored.Start(AssistantMessage("call-A"), [journaled]);

        // Assert
        Assert.Equal("netclaw", journaled.ToolFailureSource);
        Assert.Equal("invalid_rationale", journaled.ToolFailureCode);
        Assert.Null(journaled.ToolFailureScope);
        Assert.Equal(live.CycleResult(callId)?.FailureKey, restored.CycleResult(callId)?.FailureKey);
        Assert.Equal(new ToolFailureKey("netclaw", "invalid_rationale"), restored.CycleResult(callId)?.FailureKey);
    }

    [Fact]
    public void A_record_without_failure_fields_restores_no_failure_key()
    {
        // Arrange
        var legacy = Result("call-A") with
        {
            ToolOutcomeCategory = (int)ToolInvocationOutcomeCategory.InvalidInput
        };
        var batch = new ActiveToolBatchTracker();

        // Act
        batch.Start(AssistantMessage("call-A"), [legacy]);

        // Assert
        Assert.Null(batch.CycleResult(new ToolCallId("call-A"))?.FailureKey);
    }

    [Theory]
    [InlineData("success")] [InlineData("different-failure")] [InlineData("pending")]
    public void A_batch_without_the_cause_removes_its_old_advice(string outcome)
    {
        var turn = new TurnStateTracker();
        var failure = new ToolCycleResult(ToolInvocationOutcomeCategory.InvalidInput, "rejected")
            { FailureKey = new ToolFailureKey("netclaw", "invalid_rationale") };
        turn.ObserveProgress([failure, failure, failure, new(ToolInvocationOutcomeCategory.Success, "sibling")], true);
        var state = SessionState.Empty.AddSystemNudge(turn.CreateNudgeAfterResults(null, false)!);
        Assert.Contains(state.History, message => ToolCycleMessages.IsCauseNudge(message.Content));
        ToolInvocationReceipt receipt = outcome switch
        {
            "success" => new ToolInvocationReceipt.Succeeded([], null),
            "different-failure" => new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure),
            "pending" => new ToolInvocationReceipt.PendingBackgroundJob(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        var batch = new ActiveToolBatchTracker();
        batch.Start([new FunctionCallContent("next", "file_read")], null);
        var result = new ToolCallResult(Result("next"), [], [], [], AuthorizationAttemptId.New(), Receipt: receipt);
        batch.RecordOutcome(result);
        state = batch.ApplyRecordedResult(state, result.MessageWithProgress);
        batch.MarkExecutionTaskCompleted();
        var ready = Assert.IsType<BatchCompletion.Ready>(batch.CompleteIfReady(state, turn));
        Assert.DoesNotContain(ready.State.History, message => ToolCycleMessages.IsCauseNudge(message.Content));
        Assert.Equal(RefusedRepeatAction.None, turn.EvaluateProgress());
    }

    private static SerializableChatMessage AssistantMessage(string callId) => new()
    {
        Role = ChatRole.Assistant,
        ToolCalls = [new SerializableToolCall { CallId = new ToolCallId(callId), Name = new ToolName("file_read") }]
    };

    private static SerializableChatMessage Wire(SerializableChatMessage message)
        => NetclawProtoMapper.FromProto(ChatMessageProto.Parser.ParseFrom(
            NetclawProtoMapper.ToProto(message).ToByteArray()));

    private static FunctionCallContent[] Calls() =>
    [
        new FunctionCallContent("call-A", "file_read"),
        new FunctionCallContent("call-B", "file_read")
    ];

    private static SerializableChatMessage Result(string callId) => new()
    {
        Role = ChatRole.Tool,
        Name = "file_read",
        ToolCallId = new ToolCallId(callId),
        Content = $"result {callId}"
    };

    private static SerializableMediaReference Media(string path) => new()
    {
        RelativePath = path,
        MimeType = new MimeType("image/png"),
        Modality = (int)MediaModality.Image,
        FileSizeBytes = 16
    };
}
