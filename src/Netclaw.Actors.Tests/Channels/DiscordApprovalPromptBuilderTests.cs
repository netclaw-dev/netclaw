// -----------------------------------------------------------------------
// <copyright file="DiscordApprovalPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
}
