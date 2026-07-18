// -----------------------------------------------------------------------
// <copyright file="DiscordApprovalPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Discord;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

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
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("git_push"),
            DisplayText = "push to origin/main",
            Patterns = ["origin/main"],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
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
        Assert.Contains(ApprovalOptionKeys.ApproveSessionLabel, prompt);
        Assert.Contains(ApprovalOptionKeys.ApproveAlwaysLabel, prompt);
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

        var prompt = DiscordApprovalPromptBuilder.BuildTextPrompt(request);

        Assert.DoesNotContain("Pattern:", prompt);
    }

    [Fact]
    public void BuildDecisionStatus_formats_known_keys()
    {
        // Labels updated in section 7 (approval-policy-v2) — see ApprovalOptionKeys.
        // Discord prompt body redesign to single-line resolution lands in section 8;
        // for now we only assert the new label spellings make it through.
        Assert.Contains("Once", DiscordApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveOnce));
        Assert.Contains("Always here", DiscordApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveAlways));
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
            CallId = new Netclaw.Tools.ToolCallId("call-btn"),
            ToolName = new Netclaw.Tools.ToolName("exec_shell"),
            DisplayText = "rm -rf /tmp/test",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
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
            CallId = new Netclaw.Tools.ToolCallId("call-rt"),
            ToolName = new Netclaw.Tools.ToolName("tool"),
            DisplayText = "action",
            RequesterSenderId = new SenderId("user-123"),
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel)
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
            CallId = new Netclaw.Tools.ToolCallId("call-nr"),
            ToolName = new Netclaw.Tools.ToolName("tool"),
            DisplayText = "action",
            RequesterSenderId = null,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var encoded = DiscordApprovalPromptBuilder.BuildButtonValue(request, request.Options[0]);
        Assert.True(DiscordApprovalPromptBuilder.TryParseButtonValue(encoded, out var callId, out var selectedKey, out var requesterSenderId));
        Assert.Equal("call-nr", callId);
        Assert.Equal(ApprovalOptionKeys.Deny, selectedKey);
        Assert.Null(requesterSenderId);
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

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.ApproveOnce, "user-42");

        Assert.Contains(":white_check_mark:", text);
        Assert.Contains("git_push", text);
        Assert.Contains("push to origin/main", text);
        // v2 single-line resolution message replaces "**Decision:** <label>".
        Assert.Contains("Approved (no save)", text);
        Assert.Contains("<@user-42>", text);
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

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.Deny, "user-99");

        Assert.Contains(":no_entry:", text);
        // v2 single-line resolution message: "Denied" instead of "Decision: Deny".
        Assert.Contains("Denied", text);
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

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(
            request, ApprovalOptionKeys.ApproveOnce, "user-1");

        Assert.DoesNotContain("Pattern", text);
    }

    // ── v2 prompt redesign (parallel to Slack section 7) ──

    private static IReadOnlyList<ToolInteractionOption> FullButtonRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
        new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
    ];

    private static IReadOnlyList<ToolInteractionOption> MessyRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
    ];

    private static ToolInteractionRequest V2Request(
        string command,
        IReadOnlyList<string> verbs,
        string? cwd,
        IReadOnlyList<ToolInteractionOption> options,
        bool isMessy = false)
        => new()
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = command,
            Patterns = verbs,
            CandidateVerbs = verbs,
            Cwd = cwd,
            IsMessy = isMessy,
            Options = options
        };

    [Fact]
    public void Mcp_prompt_renders_invocation_without_shell_scope_chrome()
    {
        var request = V2Request(
            "Dropbox/upload(destination_directory=\"/Finance/Q3\", contents=(90000 chars, 2000 lines))",
            ["Dropbox/upload"],
            cwd: null,
            options:
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveMcpToolLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]) with
        {
            ToolName = new Netclaw.Tools.ToolName("Dropbox/upload")
        };

        var text = DiscordApprovalPromptBuilder.BuildTextPrompt(request);
        var (buttonText, buttons) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.Contains("MCP tool approval required", text);
        Assert.Contains("Invocation:", text);
        Assert.Contains("Allow this MCP tool invocation?", text);
        Assert.Contains("**Invocation:**", buttonText);
        Assert.Contains(buttons, button => button.Label == ApprovalOptionKeys.ApproveMcpToolLabel);
        Assert.DoesNotContain("no working directory", text);
        Assert.DoesNotContain("Always anywhere", text);
        Assert.DoesNotContain("• Dropbox/upload", text);
    }

    [Fact]
    public void Mcp_resolution_describes_tool_scope_not_shell_location()
    {
        var request = V2Request("Dropbox/upload(path=\"/Finance/Q3\")", ["Dropbox/upload"], null, FullButtonRow()) with
        {
            ToolName = new Netclaw.Tools.ToolName("Dropbox/upload")
        };

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(
            request,
            ApprovalOptionKeys.ApproveEverywhere,
            "user-1");

        Assert.Contains("MCP tool approval resolved", text);
        Assert.Contains("Always allowed: Dropbox/upload", text);
        Assert.DoesNotContain("anywhere", text);
    }

    [Fact]
    public void V2_single_verb_collapses_into_header()
    {
        var request = V2Request("git status", ["git status"], "/home/user/repos/foo", FullButtonRow());

        var (text, _) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.Contains("Approve git status in /home/user/repos/foo?", text);
    }

    [Fact]
    public void V2_multi_verb_uses_generic_header_with_bullets()
    {
        var request = V2Request(
            "git fetch && git rebase && git status",
            ["git fetch", "git rebase", "git status"],
            "/home/user/repos/foo",
            FullButtonRow());

        var (text, _) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.Contains("Approve in /home/user/repos/foo?", text);
        Assert.Contains("• `git fetch`", text);
        Assert.Contains("• `git rebase`", text);
        Assert.Contains("• `git status`", text);
    }

    [Fact]
    public void V2_messy_command_emits_complex_command_hint()
    {
        var request = V2Request(
            "for f in *.log; do grep ERROR \"$f\"; done",
            verbs: [],
            cwd: "/home/user/repos/foo",
            options: MessyRow(),
            isMessy: true);

        var (text, buttons) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.Contains("complex command", text);
        Assert.Equal(2, buttons.Count);
    }

    [Fact]
    public void V2_button_row_has_five_buttons_with_danger_styling_on_danger_keys()
    {
        var request = V2Request("git status", ["git status"], "/home/user/repos/foo", FullButtonRow());

        var (_, buttons) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.Equal(5, buttons.Count);
        var byLabel = buttons.ToDictionary(b => b.Label, b => b);
        Assert.Equal(DiscordButtonStyle.Success, byLabel[ApprovalOptionKeys.ApproveOnceLabel].Style);
        Assert.Equal(DiscordButtonStyle.Secondary, byLabel[ApprovalOptionKeys.ApproveSessionLabel].Style);
        Assert.Equal(DiscordButtonStyle.Secondary, byLabel[ApprovalOptionKeys.ApproveAlwaysLabel].Style);
        Assert.Equal(DiscordButtonStyle.Danger, byLabel[ApprovalOptionKeys.ApproveEverywhereLabel].Style);
        Assert.Equal(DiscordButtonStyle.Danger, byLabel[ApprovalOptionKeys.DenyLabel].Style);
    }

    [Fact]
    public void V2_resolved_text_for_always_here_uses_Saved_verbs_in_dir()
    {
        var request = V2Request("git pull && git rebase", ["git pull", "git rebase"], "/home/user/repos/foo", FullButtonRow());

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(request, ApprovalOptionKeys.ApproveAlways, "U123");

        Assert.Contains("Saved: git pull, git rebase in /home/user/repos/foo", text);
    }

    [Fact]
    public void V2_resolved_text_for_always_anywhere_uses_Saved_verbs_anywhere()
    {
        var request = V2Request("freshdesk --since=24h", ["freshdesk"], "/home/user/.netclaw/sessions/abc", FullButtonRow());

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(request, ApprovalOptionKeys.ApproveEverywhere, "U123");

        Assert.Contains("Saved: freshdesk anywhere", text);
    }

    [Fact]
    public void V2_resolved_text_for_this_chat_uses_Saved_for_this_chat()
    {
        var request = V2Request("jsonlint config.json", ["jsonlint config.json"], "/home/user/repos/foo", FullButtonRow());

        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptText(request, ApprovalOptionKeys.ApproveSession, "U123");

        Assert.Contains("Saved for this chat: jsonlint config.json in /home/user/repos/foo", text);
    }

    // Cold-spawn variant (#939): when the binding has lost its
    // ToolInteractionRequest, we still need a resolved-state message that
    // clears the action buttons.
    [Fact]
    public void Resolved_text_without_request_for_deny_uses_no_entry()
    {
        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
            ApprovalOptionKeys.Deny, "U123");

        Assert.Contains(":no_entry:", text);
        Assert.Contains("Denied", text);
        Assert.Contains("<@U123>", text);
    }

    [Fact]
    public void Resolved_text_without_request_for_approve_once_uses_checkmark()
    {
        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "U123");

        Assert.Contains(":white_check_mark:", text);
        Assert.Contains("Approved (no save)", text);
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveAlways, "Saved: always here")]
    [InlineData(ApprovalOptionKeys.ApproveEverywhere, "Saved: always anywhere")]
    [InlineData(ApprovalOptionKeys.ApproveSession, "Saved for this chat")]
    public void Resolved_text_without_request_uses_generic_resolution_phrasing(string selectedKey, string expectedFragment)
    {
        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(selectedKey, "U123");
        Assert.Contains(expectedFragment, text);
    }

    [Fact]
    public void Resolved_text_without_request_includes_persisted_tool_name_and_display_text()
    {
        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
            ApprovalOptionKeys.ApproveSession,
            "U123",
            toolName: "shell_execute",
            displayText: "gh pr create --base master --head feature/foo");

        Assert.Contains("**Tool:** `shell_execute`", text);
        Assert.Contains("gh pr create --base master --head feature/foo", text);
        Assert.Contains("Saved for this chat", text);
    }

    [Fact]
    public void Resolved_text_without_request_falls_back_to_generic_when_only_one_field_supplied()
    {
        var toolOnly = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "U123", toolName: "shell_execute", displayText: null);
        Assert.DoesNotContain("**Tool:**", toolOnly);
        Assert.Contains("Approved (no save)", toolOnly);

        var displayOnly = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "U123", toolName: null, displayText: "gh pr create");
        Assert.DoesNotContain("gh pr create", displayOnly);
        Assert.Contains("Approved (no save)", displayOnly);
    }

    [Fact]
    public void Resolved_text_without_request_truncates_oversized_persisted_display_text()
    {
        // Cold-spawn redraw must respect Discord's 2000-char cap even when the
        // persisted display text is at the 16 KB journal ceiling.
        var oversized = new string('y', 5_000);
        var text = DiscordApprovalPromptBuilder.BuildResolvedPromptTextWithoutRequest(
            ApprovalOptionKeys.ApproveSession,
            "U123",
            toolName: "shell_execute",
            displayText: oversized);

        Assert.True(text.Length < 2001, $"Resolved text length {text.Length} exceeded Discord's 2000-char cap");
    }

    [Fact]
    public void Oversized_command_keeps_prompt_under_Discord_cap()
    {
        // Discord rejects messages over 2000 chars; without truncation
        // the binding's auto-deny fallback misreports as a user decline.
        // Same failure shape as the Slack regression — see session
        // D0AC6CKBK5K/1779811366.695739.
        var oversized = new string('y', 10_000);
        var request = V2Request(oversized, ["gh issue create"], "/home/user/repos/foo", FullButtonRow());

        var textPrompt = DiscordApprovalPromptBuilder.BuildTextPrompt(request);
        var (buttonPromptText, _) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);

        Assert.True(textPrompt.Length < 2001, $"Text prompt length {textPrompt.Length} exceeded Discord's 2000-char cap");
        Assert.True(buttonPromptText.Length < 2001, $"Button prompt length {buttonPromptText.Length} exceeded Discord's 2000-char cap");
    }
}
