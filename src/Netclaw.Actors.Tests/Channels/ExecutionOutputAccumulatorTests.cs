// -----------------------------------------------------------------------
// <copyright file="ExecutionOutputAccumulatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class ExecutionOutputAccumulatorTests
{
    private static readonly SessionId TestSessionId = new("test/session");
    private static readonly ToolName TestNotifyTool = new("send_channel_message");

    [Fact]
    public void TextDeltaOutput_accumulates_text()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var action1 = acc.ProcessOutput(new TextDeltaOutput("Hello ") { SessionId = TestSessionId });
        var action2 = acc.ProcessOutput(new TextDeltaOutput("world") { SessionId = TestSessionId });

        Assert.Equal(OutputAction.Continue, action1);
        Assert.Equal(OutputAction.Continue, action2);
        Assert.Equal("Hello world", acc.GetAccumulatedText());
    }

    [Fact]
    public void TextStreamDiscarded_clears_multi_delta_accumulated_text()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        // A real multi-delta stall: two substantive deltas before the dead call
        // is discarded — a single-delta fake would not exercise the accumulator's
        // append path the way a real stalled provider stream does.
        acc.ProcessOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = TestSessionId });

        acc.ProcessOutput(new TextStreamDiscarded { SessionId = TestSessionId });

        acc.ProcessOutput(new TextDeltaOutput("Resumed answer ") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextDeltaOutput("after timeout") { SessionId = TestSessionId });

        // The delta-accumulated result must contain ONLY the resumed call's text —
        // the dead call's partial content must not survive the discard.
        Assert.Equal("Resumed answer after timeout", acc.GetAccumulatedText());
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", acc.GetAccumulatedText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TextStreamDiscarded_preserves_an_earlier_completed_calls_text_but_discards_only_the_dead_calls_partial()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        // Call 1: streams a preamble, then completes (a tool round) — TextOutput
        // marks the call boundary and commits the preamble (see D1 in the review).
        acc.ProcessOutput(new TextDeltaOutput("Checking the files now. ") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextOutput("Checking the files now. ") { SessionId = TestSessionId });

        // Call 2: streams two real deltas, then dies mid-stream and is discarded.
        acc.ProcessOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextStreamDiscarded { SessionId = TestSessionId });

        // Resumed call streams the real final answer.
        acc.ProcessOutput(new TextDeltaOutput("Done: the answer is X.") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextOutput("Done: the answer is X.") { SessionId = TestSessionId });

        // Before the fix, TextStreamDiscarded cleared the whole turn-scoped
        // buffer, wiping call 1's already-completed preamble along with call 2's
        // dead partial. The result must keep call 1's text AND the resumed
        // answer, with none of the dead call's partial.
        var result = acc.GetAccumulatedText();
        Assert.Equal("Checking the files now. Done: the answer is X.", result);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TextStreamDiscarded_lets_TextOutput_repopulate_after_discard()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new TextDeltaOutput("stalled") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextStreamDiscarded { SessionId = TestSessionId });

        // No further deltas — the resumed call's answer arrives as a single
        // non-streamed TextOutput. Before the fix, _sawTextDelta stayed true from
        // the dead call's delta, so this TextOutput would be silently ignored.
        acc.ProcessOutput(new TextOutput("Resumed answer") { SessionId = TestSessionId });

        Assert.Equal("Resumed answer", acc.GetAccumulatedText());
    }

    [Fact]
    public void TextOutput_with_IsCallBoundary_false_does_not_move_the_commit_marker_past_a_live_calls_partial_text()
    {
        // F2: EmitExpiredPromptNotice/EmitWrongRequesterApprovalNotice/
        // EmitUnavailableApprovalOptionNotice send a mid-stream TextOutput
        // (IsCallBoundary = false) while another call still streams.
        // Before the fix, ANY TextOutput advanced the commit marker over the
        // live call's partial text; a subsequent stall+discard then found
        // nothing left to remove, and the resumed call's answer glued onto
        // the dead partial.
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = TestSessionId });

        acc.ProcessOutput(new TextOutput("That approval prompt has expired.")
        {
            SessionId = TestSessionId,
            IsCallBoundary = false
        });

        acc.ProcessOutput(new TextStreamDiscarded { SessionId = TestSessionId });

        acc.ProcessOutput(new TextDeltaOutput("Done: the answer is X.") { SessionId = TestSessionId });

        var result = acc.GetAccumulatedText();
        Assert.Equal("Done: the answer is X.", result);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TextOutput_accumulates_when_no_prior_delta()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new TextOutput("Full text") { SessionId = TestSessionId });

        Assert.Equal("Full text", acc.GetAccumulatedText());
    }

    [Fact]
    public void TextOutput_ignored_after_TextDeltaOutput()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new TextDeltaOutput("streamed") { SessionId = TestSessionId });
        acc.ProcessOutput(new TextOutput("assembled") { SessionId = TestSessionId });

        Assert.Equal("streamed", acc.GetAccumulatedText());
    }

    [Fact]
    public void TurnCompleted_returns_TurnCompleted_action()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var action = acc.ProcessOutput(new TurnCompleted { SessionId = TestSessionId, TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1) });

        Assert.Equal(OutputAction.TurnCompleted, action);
    }

    [Fact]
    public void ErrorOutput_returns_Error_action_and_stores_details()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);
        var cause = new InvalidOperationException("inner");

        var action = acc.ProcessOutput(new ErrorOutput
        {
            SessionId = TestSessionId,
            Message = "Something failed",
            Category = ErrorCategory.ProviderFailure,
            Cause = cause
        });

        Assert.Equal(OutputAction.Error, action);
        Assert.Equal("Something failed", acc.LastErrorMessage);
        Assert.Equal(ErrorCategory.ProviderFailure, acc.LastErrorCategory);
        Assert.Same(cause, acc.LastErrorCause);
    }

    [Fact]
    public void BufferFlush_returns_Continue()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var action = acc.ProcessOutput(new BufferFlush { SessionId = TestSessionId });

        Assert.Equal(OutputAction.Continue, action);
    }

    [Fact]
    public void Tracks_successful_notification_tool_result()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new ToolResultOutput
        {
            SessionId = TestSessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("send_channel_message"),
            Result = "Message sent to channel C1."
        });

        Assert.True(acc.NotifyAttempted);
        Assert.False(acc.NotifyFailed);
    }

    [Fact]
    public void Tracks_failed_notification_tool_result()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new ToolResultOutput
        {
            SessionId = TestSessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-2"),
            ToolName = new Netclaw.Tools.ToolName("send_channel_message"),
            Result = "Error: channel not found"
        });

        Assert.True(acc.NotifyAttempted);
        Assert.True(acc.NotifyFailed);
    }

    [Fact]
    public void Ignores_non_notification_tool_results()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new ToolResultOutput
        {
            SessionId = TestSessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-3"),
            ToolName = new Netclaw.Tools.ToolName("web_search"),
            Result = "Found 5 results"
        });

        Assert.False(acc.NotifyAttempted);
    }

    // ── BuildNotifyFailureMessage tests ──────────────────────────────────────

    [Fact]
    public void No_failure_when_no_notify_instructions()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var result = acc.BuildNotifyFailureMessage(requiresChannelDelivery: false, deliveryRequired: true);

        Assert.Null(result);
    }

    [Fact]
    public void Required_policy_fails_when_no_notification_sent()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var result = acc.BuildNotifyFailureMessage(requiresChannelDelivery: true, deliveryRequired: true);

        Assert.Contains("no notification tool was invoked", result);
    }

    [Fact]
    public void Conditional_policy_succeeds_when_no_notification_sent()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var result = acc.BuildNotifyFailureMessage(requiresChannelDelivery: true, deliveryRequired: false);

        Assert.Null(result);
    }

    [Fact]
    public void Succeeds_when_notification_attempted_and_succeeded()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);
        acc.ProcessOutput(new ToolResultOutput
        {
            SessionId = TestSessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-ok"),
            ToolName = new Netclaw.Tools.ToolName("send_channel_message"),
            Result = "Message sent."
        });

        var result = acc.BuildNotifyFailureMessage(requiresChannelDelivery: true, deliveryRequired: true);

        Assert.Null(result);
    }

    [Fact]
    public void Fails_when_notification_attempted_and_errored()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);
        acc.ProcessOutput(new ToolResultOutput
        {
            SessionId = TestSessionId,
            CallId = new Netclaw.Tools.ToolCallId("call-err"),
            ToolName = new Netclaw.Tools.ToolName("send_channel_message"),
            Result = "Error: channel not found"
        });

        var result = acc.BuildNotifyFailureMessage(requiresChannelDelivery: true, deliveryRequired: false);

        Assert.Contains("channel not found", result);
    }
}
