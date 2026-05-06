// -----------------------------------------------------------------------
// <copyright file="MattermostApprovalPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostApprovalPromptBuilderTests
{
    [Fact]
    public void BuildTextPrompt_contains_tool_name_and_options()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-1",
            ToolName = "git_push",
            DisplayText = "push to origin/main",
            Patterns = ["origin/main"],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var prompt = MattermostApprovalPromptBuilder.BuildTextPrompt(request);

        Assert.Contains("git_push", prompt);
        Assert.Contains("push to origin/main", prompt);
        Assert.Contains("origin/main", prompt);
        Assert.Contains("A)", prompt);
        Assert.Contains("B)", prompt);
        Assert.Contains("C)", prompt);
        Assert.Contains("D)", prompt);
    }

    [Fact]
    public void BuildTextPrompt_omits_pattern_when_empty()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-2",
            ToolName = "read_file",
            DisplayText = "read config.json",
            Patterns = [],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var prompt = MattermostApprovalPromptBuilder.BuildTextPrompt(request);

        Assert.DoesNotContain("Pattern:", prompt);
    }

    [Fact]
    public void BuildDecisionStatus_formats_known_keys()
    {
        Assert.Contains("Approve once", MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveOnce));
        Assert.Contains("Approve always", MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveAlways));
        Assert.Contains("Deny", MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.Deny));
    }

    [Fact]
    public void BuildDecisionStatus_passes_through_unknown_key()
    {
        var status = MattermostApprovalPromptBuilder.BuildDecisionStatus("custom_key");
        Assert.Contains("custom_key", status);
    }

    [Fact]
    public void BuildResolvedPromptText_approve_once_shows_checkmark()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-r1",
            ToolName = "git_push",
            DisplayText = "push to origin/main",
            Patterns = ["origin/main"],
            Options = [new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)]
        };

        var text = MattermostApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.ApproveOnce, "user-42");

        Assert.Contains(":white_check_mark:", text);
        Assert.Contains("git_push", text);
        Assert.Contains("push to origin/main", text);
        Assert.Contains("origin/main", text);
        Assert.Contains(ApprovalOptionKeys.ApproveOnceLabel, text);
        Assert.Contains("@user-42", text);
    }

    [Fact]
    public void BuildResolvedPromptText_deny_shows_no_entry()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-r2",
            ToolName = "rm_file",
            DisplayText = "delete /etc/passwd",
            Options = [new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)]
        };

        var text = MattermostApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.Deny, "user-99");

        Assert.Contains(":no_entry:", text);
        Assert.Contains(ApprovalOptionKeys.DenyLabel, text);
        Assert.DoesNotContain(":white_check_mark:", text);
    }

    [Fact]
    public void BuildResolvedPromptText_omits_patterns_when_empty()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-r3",
            ToolName = "read_file",
            DisplayText = "read config.json",
            Patterns = [],
            Options = [new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)]
        };

        var text = MattermostApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.ApproveOnce, "user-1");

        Assert.DoesNotContain("Pattern", text);
    }
}
