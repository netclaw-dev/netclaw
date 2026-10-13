// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
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
        Assert.IsType<BatchCompletion.Pending>(batch.CompleteIfReady(state, turn, 10));

        state = batch.ApplyRecordedResult(state, second);
        if (!taskCompletesFirst)
        {
            Assert.IsType<BatchCompletion.Pending>(batch.CompleteIfReady(state, turn, 10));
            batch.MarkExecutionTaskCompleted();
        }

        var ready = Assert.IsType<BatchCompletion.Ready>(batch.CompleteIfReady(state, turn, 10));
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
            batch.CompleteIfReady(replay, new TurnStateTracker(), 10));

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
    [InlineData("normal", false, false, "model")]
    [InlineData("normal", false, true, "compact")]
    [InlineData("near", false, false, "model")]
    [InlineData("near", false, true, "compact")]
    [InlineData("exhausted", false, false, "text")]
    [InlineData("exhausted", false, true, "text")]
    [InlineData("exhausted", true, true, "compact")]
    [InlineData("near", true, true, "compact")]
    public void New_input_resets_budget_while_compaction_keeps_its_priority(
        string budget, bool newUserInput, bool compactionDue, string expectedAction)
    {
        // Arrange
        ToolBudgetStatus status = budget switch
        {
            "normal" => ToolBudgetStatus.Ok.Instance,
            "near" => new ToolBudgetStatus.NudgeNeeded(1, "near-budget nudge"),
            "exhausted" => new ToolBudgetStatus.Exhausted("exhausted nudge"),
            _ => throw new ArgumentOutOfRangeException(nameof(budget))
        };
        var ready = new BatchCompletion.Ready(SessionState.Empty, 1, status);

        // Act
        var action = ready.ChooseNext(newUserInput, compactionDue);

        // Assert
        switch (expectedAction)
        {
            case "model":
                var model = Assert.IsType<ToolBatchNextAction.ContinueModel>(action);
                Assert.Equal(newUserInput || budget == "normal" ? null : "near-budget nudge", model.NudgeText);
                break;
            case "compact":
                var compact = Assert.IsType<ToolBatchNextAction.RequestCompaction>(action);
                Assert.Equal(!newUserInput && budget == "near" ? "near-budget nudge" : null, compact.NudgeText);
                break;
            case "text":
                Assert.Equal("exhausted nudge", Assert.IsType<ToolBatchNextAction.ContinueWithoutTools>(action).NudgeText);
                break;
        }
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
