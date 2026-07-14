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
}
