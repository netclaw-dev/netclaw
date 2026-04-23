using Netclaw.Actors.Protocol;
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordApprovalPromptBuilderTests
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

        var prompt = DiscordApprovalPromptBuilder.BuildTextPrompt(request);

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

        var prompt = DiscordApprovalPromptBuilder.BuildTextPrompt(request);

        Assert.DoesNotContain("Pattern:", prompt);
    }

    [Fact]
    public void BuildDecisionStatus_formats_known_keys()
    {
        Assert.Contains("Approve once", DiscordApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveOnce));
        Assert.Contains("Approve always", DiscordApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveAlways));
        Assert.Contains("Deny", DiscordApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.Deny));
    }

    [Fact]
    public void BuildDecisionStatus_passes_through_unknown_key()
    {
        var status = DiscordApprovalPromptBuilder.BuildDecisionStatus("custom_key");
        Assert.Contains("custom_key", status);
    }

    [Fact]
    public void BuildButtonPrompt_returns_button_per_option()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-btn",
            ToolName = "exec_shell",
            DisplayText = "rm -rf /tmp/test",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var (text, buttons) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.Contains("exec_shell", text);
        Assert.Contains("rm -rf /tmp/test", text);
        Assert.Contains("approval", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, buttons.Count);
        Assert.Equal(ApprovalOptionKeys.ApproveOnceLabel, buttons[0].Label);
        Assert.Equal(ApprovalOptionKeys.DenyLabel, buttons[2].Label);
        Assert.Equal(DiscordButtonStyle.Danger, buttons[2].Style);
        Assert.Equal(DiscordButtonStyle.Success, buttons[0].Style);
    }

    [Fact]
    public void BuildButtonValue_roundtrips_with_TryParseButtonValue()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-rt",
            ToolName = "tool",
            DisplayText = "action",
            RequesterSenderId = "user-123",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)
            ]
        };

        var encoded = DiscordApprovalPromptBuilder.BuildButtonValue(request, request.Options[0]);
        Assert.True(DiscordApprovalPromptBuilder.TryParseButtonValue(encoded, out var callId, out var selectedKey, out var requesterSenderId));
        Assert.Equal("call-rt", callId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, selectedKey);
        Assert.Equal("user-123", requesterSenderId);
    }

    [Fact]
    public void TryParseButtonValue_returns_false_for_empty_string()
    {
        Assert.False(DiscordApprovalPromptBuilder.TryParseButtonValue("", out _, out _, out _));
        Assert.False(DiscordApprovalPromptBuilder.TryParseButtonValue(null, out _, out _, out _));
    }

    [Fact]
    public void TryParseButtonValue_returns_false_for_single_segment()
    {
        Assert.False(DiscordApprovalPromptBuilder.TryParseButtonValue("call-only", out _, out _, out _));
    }

    [Fact]
    public void TryParseButtonValue_handles_missing_requester()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = "call-nr",
            ToolName = "tool",
            DisplayText = "action",
            RequesterSenderId = null,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var encoded = DiscordApprovalPromptBuilder.BuildButtonValue(request, request.Options[0]);
        Assert.True(DiscordApprovalPromptBuilder.TryParseButtonValue(encoded, out var callId, out var selectedKey, out var requesterSenderId));
        Assert.Equal("call-nr", callId);
        Assert.Equal(ApprovalOptionKeys.Deny, selectedKey);
        Assert.Null(requesterSenderId);
    }
}
