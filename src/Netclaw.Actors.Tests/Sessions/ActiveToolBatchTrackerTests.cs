// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
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
        for (var i = 0; i < 9; i++)
        {
            var call = new FunctionCallContent($"call-{i}", $"sample/{i}");
            batch.Start([call], new PreparedToolCycleBatch(new ToolActionSignature($"action-{i}"),
                [new PreparedToolCycleCall(new ToolCallId(call.CallId), new ToolName(call.Name), $"args-{i}")]));
            var message = Result(call.CallId);
            var receipt = i < 8 ? (ToolInvocationReceipt)new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput)
                : new ToolInvocationReceipt.Succeeded([], null);
            batch.RecordOutcome(new ToolCallResult(message, [], [], [], AuthorizationAttemptId.New(), Receipt: receipt));
            state = batch.ApplyRecordedResult(state, message);
            batch.MarkExecutionTaskCompleted();
            var ready = Assert.IsType<BatchCompletion.Ready>(batch.CompleteIfReady(state, turn));
            state = ready.State;
            var nudges = state.History.Where(message => ToolCycleMessages.IsLoopNudge(message.Content)).ToArray();
            if (i is >= 3 and < 8)
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
