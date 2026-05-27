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
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("git_push"),
            DisplayText = "push to origin/main",
            Patterns = ["origin/main"],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
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
        Assert.Contains("E)", prompt);
        Assert.Contains(ApprovalOptionKeys.ApproveEverywhereLabel, prompt);
    }

    [Fact]
    public void BuildTextPrompt_omits_pattern_when_empty()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-2"),
            ToolName = new Netclaw.Tools.ToolName("read_file"),
            DisplayText = "read config.json",
            Patterns = [],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var prompt = MattermostApprovalPromptBuilder.BuildTextPrompt(request);

        Assert.DoesNotContain("Pattern:", prompt);
    }

    [Fact]
    public void BuildDecisionStatus_formats_known_keys()
    {
        Assert.Contains(ApprovalOptionKeys.ApproveOnceLabel, MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveOnce));
        Assert.Contains(ApprovalOptionKeys.ApproveAlwaysLabel, MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveAlways));
        Assert.Contains(ApprovalOptionKeys.DenyLabel, MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.Deny));
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
            CallId = new Netclaw.Tools.ToolCallId("call-r1"),
            ToolName = new Netclaw.Tools.ToolName("git_push"),
            DisplayText = "push to origin/main",
            Patterns = ["origin/main"],
            Options = [new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel)]
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
            CallId = new Netclaw.Tools.ToolCallId("call-r2"),
            ToolName = new Netclaw.Tools.ToolName("rm_file"),
            DisplayText = "delete /etc/passwd",
            Options = [new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)]
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
            CallId = new Netclaw.Tools.ToolCallId("call-r3"),
            ToolName = new Netclaw.Tools.ToolName("read_file"),
            DisplayText = "read config.json",
            Patterns = [],
            Options = [new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel)]
        };

        var text = MattermostApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.ApproveOnce, "user-1");

        Assert.DoesNotContain("Pattern", text);
    }

    [Fact]
    public void BuildButtonPrompt_produces_attachment_with_five_buttons()
    {
        var request = CreateStandardRequest();

        var (text, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost:5199/api/mattermost/actions", "ch-1", "root-post-1");

        Assert.Contains("Tool approval required", text);
        Assert.Contains("git_push", text);
        Assert.Contains("reply with `A`, `B`, `C`, `D`, `E`", text);

        Assert.Single(attachments);
        var attachment = attachments[0];
        Assert.NotNull(attachment.Actions);
        Assert.Equal(5, attachment.Actions!.Count);
    }

    [Fact]
    public void BuildButtonPrompt_buttons_encode_context_correctly()
    {
        var request = CreateStandardRequest();
        var actionStore = new MattermostCallbackActionStore(TimeProvider.System);

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://callback:5199/api/mattermost/actions", "ch-1", "root-post-1", actionStore);

        var approveOnce = attachments[0].Actions![0];
        Assert.Equal("tool_approval_approve_once", approveOnce.Id);
        Assert.Equal(ApprovalOptionKeys.ApproveOnceLabel, approveOnce.Name);
        Assert.Equal("http://callback:5199/api/mattermost/actions", approveOnce.IntegrationUrl);
        Assert.Contains("action_token", approveOnce.Context.Keys);
        Assert.NotEmpty(approveOnce.Context["action_token"]);
    }

    [Fact]
    public void BuildButtonPrompt_deny_button_has_danger_style()
    {
        var request = CreateStandardRequest();

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1");

        var denyButton = attachments[0].Actions!.Single(a => a.Id == "tool_approval_deny");
        Assert.Equal("danger", denyButton.Style);
    }

    [Fact]
    public void BuildButtonPrompt_approve_once_has_primary_style()
    {
        var request = CreateStandardRequest();

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1");

        var approveOnce = attachments[0].Actions!.Single(a => a.Id == "tool_approval_approve_once");
        Assert.Equal("primary", approveOnce.Style);
    }

    [Fact]
    public void BuildResolvedAttachment_approve_shows_green_color()
    {
        var request = CreateStandardRequest();
        var attachment = MattermostApprovalPromptBuilder.BuildResolvedAttachment(
            request, ApprovalOptionKeys.ApproveOnce, "user-42");

        Assert.Equal("#2EA44F", attachment.Color);
        Assert.Contains(":white_check_mark:", attachment.Text!);
        Assert.Contains("git_push", attachment.Text!);
        Assert.Contains("@user-42", attachment.Text!);
        Assert.Null(attachment.Actions);
    }

    [Fact]
    public void BuildResolvedAttachment_deny_shows_red_color()
    {
        var request = CreateStandardRequest();
        var attachment = MattermostApprovalPromptBuilder.BuildResolvedAttachment(
            request, ApprovalOptionKeys.Deny, "user-99");

        Assert.Equal("#CC0000", attachment.Color);
        Assert.Contains(":no_entry:", attachment.Text!);
        Assert.Null(attachment.Actions);
    }

    // Cold-spawn variant (#939): when the binding has lost its
    // ToolInteractionRequest we still need a resolved-state attachment that
    // clears the buttons. These tests pin its content and styling.
    [Fact]
    public void BuildResolvedAttachmentWithoutRequest_deny_uses_red_color_and_denied_label()
    {
        var attachment = MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
            ApprovalOptionKeys.Deny, "user-99");

        Assert.Equal("#CC0000", attachment.Color);
        Assert.Contains(":no_entry:", attachment.Text!);
        Assert.Contains("Deny", attachment.Text!);
        Assert.Contains("user-99", attachment.Text!);
        Assert.Null(attachment.Actions);
    }

    [Fact]
    public void BuildResolvedAttachmentWithoutRequest_approve_once_uses_green_color()
    {
        var attachment = MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "user-99");

        Assert.Equal("#2EA44F", attachment.Color);
        Assert.Contains(":white_check_mark:", attachment.Text!);
        Assert.Null(attachment.Actions);
    }

    [Fact]
    public void BuildButtonPrompt_with_action_store_includes_action_token()
    {
        var request = CreateStandardRequest();
        var actionStore = new MattermostCallbackActionStore(TimeProvider.System);

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", actionStore);

        foreach (var action in attachments[0].Actions!)
        {
            Assert.True(action.Context.ContainsKey("action_token"), $"Button '{action.Id}' missing action token");
            Assert.NotEmpty(action.Context["action_token"]);
        }
    }

    [Fact]
    public void BuildButtonPrompt_without_action_store_omits_action_token()
    {
        var request = CreateStandardRequest();

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1");

        foreach (var action in attachments[0].Actions!)
        {
            Assert.False(action.Context.ContainsKey("action_token"), $"Button '{action.Id}' should not have action token");
        }
    }

    [Fact]
    public void BuildButtonPrompt_action_token_resolves_expected_action_once()
    {
        var request = CreateStandardRequest();
        var actionStore = new MattermostCallbackActionStore(TimeProvider.System);

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", actionStore);

        var approveOnce = attachments[0].Actions![0];
        Assert.True(actionStore.TryConsume(approveOnce.Context["action_token"], out var stored));
        Assert.NotNull(stored);
        Assert.Equal("ch-1", stored!.ChannelId);
        Assert.Equal("call-btn-1", stored.CallId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, stored.SelectedKey);
        Assert.Equal("root-post-1", stored.RootPostId);
        Assert.Equal("requester-1", stored.RequesterSenderId);
        Assert.False(actionStore.TryConsume(approveOnce.Context["action_token"], out _));
    }

    private static ToolInteractionRequest CreateStandardRequest()
        => new()
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-btn-1"),
            ToolName = new Netclaw.Tools.ToolName("git_push"),
            DisplayText = "push to origin/main",
            RequesterSenderId = new SenderId("requester-1"),
            Patterns = ["origin/main"],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

    [Fact]
    public void Oversized_command_keeps_prompt_under_Mattermost_cap()
    {
        // Mattermost's hard message cap is 16K, but approval prompts
        // bypass ChunkMessage. Without truncation the post fails outright
        // and the binding auto-denies, misreported to the model.
        var oversized = new string('z', 30_000);
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-big-1"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = oversized,
            RequesterSenderId = new SenderId("requester-1"),
            Patterns = ["gh issue create"],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var textPrompt = MattermostApprovalPromptBuilder.BuildTextPrompt(request);
        var (buttonText, _) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "https://callback.example/url", channelId: "ch-1", rootPostId: "root-1");

        Assert.True(textPrompt.Length < 16_001, $"Text prompt length {textPrompt.Length} exceeded Mattermost's 16000-char cap");
        Assert.True(buttonText.Length < 16_001, $"Button prompt length {buttonText.Length} exceeded Mattermost's 16000-char cap");
    }
}
