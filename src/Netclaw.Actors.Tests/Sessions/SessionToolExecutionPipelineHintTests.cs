// -----------------------------------------------------------------------
// <copyright file="SessionToolExecutionPipelineHintTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Direct unit tests for the deny-path hint helper that points the agent at
/// <c>set_working_directory</c> when a shell call is denied for cwd-outside-
/// safe-spaces. Exercises the helper in isolation rather than the whole
/// pipeline so the test stays focused on the hint-emission logic.
/// </summary>
public sealed class SessionToolExecutionPipelineHintTests
{
    private const string ShellTool = "shell_execute";

    [Fact]
    public void Hint_emitted_when_shell_denied_with_cwd_outside_both_safe_spaces()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: "/home/user/repos/bar",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true);

        Assert.Contains("set_working_directory", hint);
        Assert.Contains("/home/user/repos/bar", hint);
    }

    [Fact]
    public void Hint_suppressed_when_set_working_directory_is_unavailable()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: "/home/user/repos/bar",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: false);

        Assert.Empty(hint);
    }

    [Fact]
    public void Hint_suppressed_when_decision_is_not_Denied()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.TimedOut,
            cwd: "/home/user/repos/bar",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true);

        Assert.Empty(hint);
    }

    [Fact]
    public void Hint_suppressed_for_non_shell_tools()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: "file_write",
            decision: ApprovalDecision.Denied,
            cwd: "/home/user/repos/bar",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true);

        Assert.Empty(hint);
    }

    [Fact]
    public void Hint_suppressed_when_cwd_is_inside_session_directory()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: "/home/user/.netclaw/sessions/abc/sub",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true);

        Assert.Empty(hint);
    }

    [Fact]
    public void Hint_suppressed_when_cwd_is_inside_project_directory()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: "/home/user/repos/foo/sub",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: "/home/user/repos/foo",
            setWorkingDirectoryAvailable: true);

        Assert.Empty(hint);
    }

    [Fact]
    public void Hint_suppressed_when_cwd_is_null()
    {
        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: null,
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true);

        Assert.Empty(hint);
    }
}
