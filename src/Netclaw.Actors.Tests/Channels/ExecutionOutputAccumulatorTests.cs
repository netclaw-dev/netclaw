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

namespace Netclaw.Actors.Tests.Channels;

public sealed class ExecutionOutputAccumulatorTests
{
    private static readonly SessionId TestSessionId = new("test/session");
    private static readonly ToolName TestNotifyTool = new("send_slack_message");

    [Fact]
    public void TextDeltaOutput_accumulates_text()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var action1 = acc.ProcessOutput(new TextDeltaOutput { SessionId = TestSessionId, Delta = "Hello " });
        var action2 = acc.ProcessOutput(new TextDeltaOutput { SessionId = TestSessionId, Delta = "world" });

        Assert.Equal(OutputAction.Continue, action1);
        Assert.Equal(OutputAction.Continue, action2);
        Assert.Equal("Hello world", acc.GetAccumulatedText());
    }

    [Fact]
    public void TextOutput_accumulates_when_no_prior_delta()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new TextOutput { SessionId = TestSessionId, Text = "Full text" });

        Assert.Equal("Full text", acc.GetAccumulatedText());
    }

    [Fact]
    public void TextOutput_ignored_after_TextDeltaOutput()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        acc.ProcessOutput(new TextDeltaOutput { SessionId = TestSessionId, Delta = "streamed" });
        acc.ProcessOutput(new TextOutput { SessionId = TestSessionId, Text = "assembled" });

        Assert.Equal("streamed", acc.GetAccumulatedText());
    }

    [Fact]
    public void TurnCompleted_returns_TurnCompleted_action()
    {
        var acc = new ExecutionOutputAccumulator(TestNotifyTool);

        var action = acc.ProcessOutput(new TurnCompleted { SessionId = TestSessionId, TurnNumber = 1 });

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
            CallId = "call-1",
            ToolName = "send_slack_message",
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
            CallId = "call-2",
            ToolName = "send_slack_message",
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
            CallId = "call-3",
            ToolName = "web_search",
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
            CallId = "call-ok",
            ToolName = "send_slack_message",
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
            CallId = "call-err",
            ToolName = "send_slack_message",
            Result = "Error: channel not found"
        });

        var result = acc.BuildNotifyFailureMessage(requiresChannelDelivery: true, deliveryRequired: false);

        Assert.Contains("channel not found", result);
    }
}
