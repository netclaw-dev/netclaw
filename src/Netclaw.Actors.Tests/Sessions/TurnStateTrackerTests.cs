// -----------------------------------------------------------------------
// <copyright file="TurnStateTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers <see cref="TurnStateTracker.EvaluateEmptyResponse"/>: a thinking-only
/// response (reasoning emitted, no final answer) must get a thinking-specific
/// nudge, and the post-tool path must tolerate several retries before failing
/// the turn.
/// </summary>
public sealed class TurnStateTrackerTests
{
    private const string ThinkingNudgeMarker = "only reasoning";

    public enum ToolPhase
    {
        BeforeAnyToolUse,
        AfterToolUse,
    }

    [Theory]
    [InlineData(ToolPhase.AfterToolUse, LlmResponseKind.ThinkingOnly)]
    [InlineData(ToolPhase.AfterToolUse, LlmResponseKind.Empty)]
    [InlineData(ToolPhase.BeforeAnyToolUse, LlmResponseKind.ThinkingOnly)]
    [InlineData(ToolPhase.BeforeAnyToolUse, LlmResponseKind.Empty)]
    public void EmptyResponse_NudgeMatchesResponseKind(ToolPhase phase, LlmResponseKind kind)
    {
        var tracker = new TurnStateTracker();
        if (phase == ToolPhase.AfterToolUse)
            tracker.RecordToolCompletion(resultCount: 1, maxToolCallsPerTurn: 30);

        var action = tracker.EvaluateEmptyResponse(kind);

        var retry = Assert.IsType<EmptyResponseAction.Retry>(action);
        if (kind == LlmResponseKind.ThinkingOnly)
            Assert.Contains(ThinkingNudgeMarker, retry.NudgeText);
        else
            Assert.DoesNotContain(ThinkingNudgeMarker, retry.NudgeText);
    }

    [Fact]
    public void PostToolThinkingOnly_RetriesSeveralTimesBeforeFailing()
    {
        var tracker = new TurnStateTracker();
        tracker.RecordToolCompletion(resultCount: 1, maxToolCallsPerTurn: 30);

        // The first three consecutive thinking-only responses retry.
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly));
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly));
        Assert.IsType<EmptyResponseAction.Retry>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly));

        // Only the fourth fails the turn.
        Assert.IsType<EmptyResponseAction.Fail>(tracker.EvaluateEmptyResponse(LlmResponseKind.ThinkingOnly));
    }
}
