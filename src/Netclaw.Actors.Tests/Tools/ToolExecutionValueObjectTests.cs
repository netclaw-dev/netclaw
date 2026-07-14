// -----------------------------------------------------------------------
// <copyright file="ToolExecutionValueObjectTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Media;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolExecutionValueObjectTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Inline_output_budget_rejects_non_positive_values(int value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new InlineOutputBudget(value));

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Execution_timeout_rejects_non_positive_values(int milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ToolExecutionTimeout(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Execution_timeout_accepts_infinite()
    {
        var timeout = new ToolExecutionTimeout(Timeout.InfiniteTimeSpan);

        Assert.Equal(Timeout.InfiniteTimeSpan, timeout.Value);
    }

    [Fact]
    public void Bound_session_rejects_missing_identity()
        => Assert.Throws<ArgumentException>(() => new ToolSessionScope.Bound(" ", null));

    [Fact]
    public void Context_rejects_missing_semantic_values()
    {
        var missingBudget = new ToolRunScope
        {
            Session = new ToolSessionScope.Sessionless(),
            Audience = TrustAudience.Public,
            InlineOutputBudget = null!,
        };
        var validScope = new ToolRunScope
        {
            Session = new ToolSessionScope.Sessionless(),
            Audience = TrustAudience.Public,
            InlineOutputBudget = InlineOutputBudget.Default,
        };

        Assert.Throws<ArgumentNullException>(
            () => new ToolExecutionContext(missingBudget, ToolExecutionTimeout.Default));
        Assert.Throws<ArgumentNullException>(
            () => new ToolExecutionContext(validScope, null!));
    }

    [Fact]
    public void Run_scope_snapshots_recent_files()
    {
        var recentFiles = new List<string> { "/repo/one.cs" };
        var runScope = new ToolRunScope
        {
            Session = new ToolSessionScope.Sessionless(),
            Audience = TrustAudience.Personal,
            InlineOutputBudget = InlineOutputBudget.Default,
            RecentFiles = recentFiles,
        };

        recentFiles.Add("/repo/two.cs");

        Assert.Equal(["/repo/one.cs"], runScope.RecentFiles);
    }

    [Fact]
    public void Output_views_are_not_mutable_backing_lists_and_forks_share_only_the_callback()
    {
        var notifications = new List<SubAgentNotificationInfo>();
        var outputs = new ToolExecutionOutputs(notifications.Add);
        outputs.AddFileAttachment("/tmp/one.txt", "one.txt", new MimeType("text/plain"));
        var fork = outputs.Fork();
        var notification = new SubAgentNotificationInfo
        {
            RunId = new SubAgentRunId("run-1"),
            AgentName = "reviewer",
            IsStarted = true,
        };

        fork.ReportSubAgentActivity(notification);

        Assert.IsNotType<List<FileAttachmentInfo>>(outputs.FileAttachments);
        Assert.Empty(fork.FileAttachments);
        Assert.Equal([notification], notifications);
    }

    [Fact]
    public void Calls_share_only_the_immutable_run_scope()
    {
        var runScope = new ToolRunScope
        {
            Session = new ToolSessionScope.Bound("slack/thread-1", "/tmp/session"),
            Audience = TrustAudience.Personal,
            InlineOutputBudget = InlineOutputBudget.Default,
        };
        var first = new ToolExecutionContext(runScope, ToolExecutionTimeout.Default);
        var second = new ToolExecutionContext(runScope, ToolExecutionTimeout.Default);

        first.Outputs.AddFileAttachment("/tmp/one.txt", "one.txt", new MimeType("text/plain"));
        first.Approval.ApplyDecision("allow-once", "shell_execute:git status");

        Assert.Same(runScope, first.RunScope);
        Assert.Same(runScope, second.RunScope);
        Assert.NotSame(first.Outputs, second.Outputs);
        Assert.NotSame(first.Approval, second.Approval);
        Assert.Empty(second.Outputs.FileAttachments);
        Assert.Null(second.Approval.AppliedDecision);
    }

    [Fact]
    public void Pipeline_execution_state_is_not_a_tool_invocation_context()
    {
        var execution = TestToolExecutionContext.CreateUnbound();

        Assert.False(typeof(ToolInvocationContext).IsAssignableFrom(typeof(ToolExecutionContext)));
        Assert.NotSame(execution, execution.Invocation);
    }
}
