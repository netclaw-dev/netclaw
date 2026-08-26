// -----------------------------------------------------------------------
// <copyright file="SessionToolExecutionPipelineHintTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
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

    [Fact]
    public void Denied_platform_temp_retry_recommends_session_scratch()
    {
        var context = new ToolApprovalContext(
            ToolName: ShellTool,
            DisplayText: "diagnostic-command",
            Patterns: ["diagnostic-command"],
            CandidateVerbs: ["diagnostic-command"],
            Options: [])
        {
            IsSessionScratchRetry = true,
            SessionScratchDirectory = "/home/user/.netclaw/sessions/abc",
            PlatformTemporaryRoot = "/tmp"
        };

        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: "/tmp",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true,
            approvalContext: context);

        Assert.Contains("private session scratch", hint);
        Assert.Contains("/home/user/.netclaw/sessions/abc", hint);
        Assert.DoesNotContain("set_working_directory", hint);
    }

    [Fact]
    public void Undeclarable_foreign_cwd_has_no_project_hint()
    {
        var invocation = TestToolExecutionContext.CreateBound(
            "signalr/example",
            "/home/user/.netclaw/sessions/abc",
            TrustAudience.Personal).Invocation;

        var hint = SessionToolExecutionPipeline.BuildSetWorkingDirectoryHint(
            toolName: ShellTool,
            decision: ApprovalDecision.Denied,
            cwd: "/outside/project",
            sessionDirectory: "/home/user/.netclaw/sessions/abc",
            projectDirectory: null,
            setWorkingDirectoryAvailable: true,
            invocation: invocation,
            canDeclare: static (_, _) => false);

        Assert.Empty(hint);
    }

    [Fact]
    public void Project_scope_correction_reports_only_the_failure_reason()
    {
        var context = new ToolApprovalContext(
            ToolName: ShellTool,
            DisplayText: "head -40 src/file.cs",
            Patterns: ["head"],
            CandidateVerbs: ["head"],
            Options: [])
        {
            SuggestedProjectDirectory = "/home/user/repos/project"
        };

        var correction = SessionToolExecutionPipeline.BuildProjectScopeDeclarationCorrection(
            context,
            setWorkingDirectoryAvailable: true);

        Assert.Contains("working_directory_not_declared", correction);
        Assert.DoesNotContain("set_working_directory", correction);
        Assert.Contains("Project directory: '/home/user/repos/project'", correction);
        Assert.DoesNotContain("Next action", correction);
    }

    [Fact]
    public void Project_scope_correction_is_suppressed_when_tool_is_unavailable()
    {
        var context = new ToolApprovalContext(
            ToolName: ShellTool,
            DisplayText: "head -40 src/file.cs",
            Patterns: ["head"],
            CandidateVerbs: ["head"],
            Options: [])
        {
            SuggestedProjectDirectory = "/home/user/repos/project"
        };

        var correction = SessionToolExecutionPipeline.BuildProjectScopeDeclarationCorrection(
            context,
            setWorkingDirectoryAvailable: false);

        Assert.Empty(correction);
    }
}
