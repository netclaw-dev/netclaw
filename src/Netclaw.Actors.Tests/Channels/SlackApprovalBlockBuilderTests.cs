// -----------------------------------------------------------------------
// <copyright file="SlackApprovalBlockBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Slack;
using Netclaw.Tools;
using SlackNet.Blocks;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackApprovalBlockBuilderTests
{
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

    private static ToolInteractionRequest Request(
        string command,
        IReadOnlyList<string> verbs,
        string? cwd,
        IReadOnlyList<ToolInteractionOption> options,
        bool isMessy = false)
        => new()
        {
            SessionId = new SessionId("signalr/test"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = command,
            RequesterSenderId = new SenderId("device-1"),
            Patterns = verbs,
            CandidateVerbs = verbs,
            Cwd = cwd,
            IsMessy = isMessy,
            Options = options
        };

    [Fact]
    public void Single_verb_collapses_into_header_line()
    {
        var request = Request("git status", ["git status"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains("Approve git status in /home/user/repos/foo?", text);
        Assert.DoesNotContain("• `git status`", text); // No redundant bullet for single-verb
    }

    [Fact]
    public void Multi_verb_uses_generic_header_with_bulleted_verbs()
    {
        var request = Request(
            "git fetch && git rebase && git status",
            ["git fetch", "git rebase", "git status"],
            "/home/user/repos/foo",
            FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains("Approve in /home/user/repos/foo?", text);
        Assert.Contains("• `git fetch`", text);
        Assert.Contains("• `git rebase`", text);
        Assert.Contains("• `git status`", text);
    }

    [Fact]
    public void Mcp_prompt_renders_invocation_without_shell_scope_chrome()
    {
        var request = Request(
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
            ToolName = new ToolName("Dropbox/upload")
        };

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);
        var blocks = string.Join('\n', SlackApprovalBlockBuilder.BuildApprovalBlocks(request)
            .OfType<SectionBlock>()
            .Select(block => block.Text is Markdown markdown ? markdown.Text : string.Empty));

        Assert.Contains("MCP tool approval required", text);
        Assert.Contains("Allow this MCP tool invocation?", text);
        Assert.Contains("*Invocation:*", blocks);
        Assert.DoesNotContain("no working directory", text);
        Assert.DoesNotContain("Always anywhere", text);
        Assert.DoesNotContain("• `Dropbox/upload`", text);
    }

    [Fact]
    public void Mcp_resolution_describes_tool_scope_not_shell_location()
    {
        var request = Request("Dropbox/upload(path=\"/Finance/Q3\")", ["Dropbox/upload"], null, FullButtonRow()) with
        {
            ToolName = new ToolName("Dropbox/upload")
        };

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
            request,
            ApprovalOptionKeys.ApproveEverywhere,
            "U123");

        Assert.Contains("MCP tool approval resolved", text);
        Assert.Contains("Always allowed: Dropbox/upload", text);
        Assert.DoesNotContain("anywhere", text);
    }

    [Fact]
    public void Messy_command_emits_complex_command_hint()
    {
        var request = Request(
            "for f in *.log; do grep ERROR \"$f\"; done",
            verbs: [],
            cwd: "/home/user/repos/foo",
            options: MessyRow(),
            isMessy: true);

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains("complex command", text);
        Assert.Contains("only one-shot approval available", text);
    }

    [Fact]
    public void Approval_blocks_render_five_buttons_with_danger_styling_on_danger_options()
    {
        var request = Request("git status", ["git status"], "/home/user/repos/foo", FullButtonRow());

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var buttons = actions.Elements.OfType<Button>().ToList();

        Assert.Equal(5, buttons.Count);

        var byKey = buttons.ToDictionary(b => b.ActionId.Split('_').Last(), b => b);
        Assert.Equal(ButtonStyle.Primary, byKey["once"].Style);
        Assert.Equal(ButtonStyle.Default, byKey["session"].Style);
        Assert.Equal(ButtonStyle.Default, byKey["always"].Style);
        Assert.Equal(ButtonStyle.Danger, byKey["everywhere"].Style);
        Assert.Equal(ButtonStyle.Danger, byKey["deny"].Style);
    }

    [Fact]
    public void Approval_blocks_omit_legacy_directory_roots_section()
    {
        var request = Request("grep error /var/log/syslog", ["grep error /var/log/syslog"], "/var/log", FullButtonRow());

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var sections = blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(sections, t => t.Contains("Directory Roots", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, t => t.Contains("*Patterns*", StringComparison.Ordinal));
    }

    // ── Resolution message single-line format ──

    [Fact]
    public void Resolved_text_for_always_here_uses_Saved_verbs_in_dir()
    {
        var request = Request("git pull && git rebase", ["git pull", "git rebase"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveAlways, "U123");

        Assert.Contains("Saved: git pull, git rebase in /home/user/repos/foo", text);
    }

    [Fact]
    public void Resolved_text_for_always_anywhere_uses_Saved_verbs_anywhere()
    {
        var request = Request("freshdesk --since=24h", ["freshdesk"], "/home/user/.netclaw/sessions/abc", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveEverywhere, "U123");

        Assert.Contains("Saved: freshdesk anywhere", text);
    }

    [Fact]
    public void Resolved_text_for_this_chat_uses_Saved_for_this_chat()
    {
        var request = Request("jsonlint config.json", ["jsonlint config.json"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveSession, "U123");

        Assert.Contains("Saved for this chat: jsonlint config.json in /home/user/repos/foo", text);
    }

    [Fact]
    public void Resolved_text_for_once_uses_Approved_no_save()
    {
        var request = Request("docker build .", ["docker build"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveOnce, "U123");

        Assert.Contains("Approved (no save)", text);
    }

    [Fact]
    public void Resolved_text_for_deny_uses_Denied()
    {
        var request = Request("rm -rf /", ["rm"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.Deny, "U123");

        Assert.Contains("Denied", text);
    }

    // Cold-spawn variant (#939): when the binding has lost its
    // ToolInteractionRequest we still need a resolved-state message so the
    // buttons clear. These tests pin the wire content of the minimal banner.
    [Fact]
    public void Resolved_text_without_request_for_deny_uses_Denied()
    {
        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(
            ApprovalOptionKeys.Deny, "U123");

        Assert.Contains(":no_entry:", text);
        Assert.Contains("Denied", text);
        Assert.Contains("U123", text);
    }

    [Fact]
    public void Resolved_text_without_request_for_approve_once_uses_checkmark()
    {
        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(
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
        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(selectedKey, "U123");
        Assert.Contains(expectedFragment, text);
    }

    [Fact]
    public void Resolved_blocks_without_request_have_no_action_buttons()
    {
        var blocks = SlackApprovalBlockBuilder.BuildResolvedApprovalBlocksWithoutRequest(
            ApprovalOptionKeys.Deny, "U123");

        Assert.NotEmpty(blocks);
        Assert.DoesNotContain(blocks, b => b is ActionsBlock);
    }

    [Fact]
    public void Resolved_text_without_request_includes_persisted_tool_name_and_display_text()
    {
        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(
            ApprovalOptionKeys.ApproveSession,
            "U123",
            toolName: "shell_execute",
            displayText: "gh pr create --base master --head feature/foo");

        Assert.Contains("`shell_execute`", text);
        Assert.Contains("gh pr create --base master --head feature/foo", text);
        Assert.Contains("Saved for this chat", text);
    }

    [Fact]
    public void Resolved_blocks_without_request_render_tool_request_section_when_persisted_summary_supplied()
    {
        var blocks = SlackApprovalBlockBuilder.BuildResolvedApprovalBlocksWithoutRequest(
            ApprovalOptionKeys.ApproveSession,
            "U123",
            toolName: "shell_execute",
            displayText: "gh pr create");

        Assert.NotEmpty(blocks);
        // No action buttons in cold-spawn redraw — the prompt is being cleared.
        Assert.DoesNotContain(blocks, b => b is ActionsBlock);

        var rendered = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(b => b.Text is Markdown md ? md.Text : string.Empty));
        Assert.Contains("`shell_execute`", rendered);
        Assert.Contains("gh pr create", rendered);
        Assert.Contains("Saved for this chat", rendered);
    }

    [Fact]
    public void Resolved_without_request_falls_back_to_generic_when_only_one_field_supplied()
    {
        // Backward-compat guard: legacy journals that supplied only the tool name
        // (or only the display text) should not produce a half-rendered Tool/Request
        // section. Both must be present, otherwise fall through to the generic
        // banner.
        var toolOnly = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "U123", toolName: "shell_execute", displayText: null);
        Assert.DoesNotContain("`shell_execute`", toolOnly);
        Assert.Contains("Approved (no save)", toolOnly);

        var displayOnly = SlackApprovalBlockBuilder.BuildResolvedApprovalTextWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "U123", toolName: null, displayText: "gh pr create");
        Assert.DoesNotContain("gh pr create", displayOnly);
        Assert.Contains("Approved (no save)", displayOnly);
    }

    [Fact]
    public void Resolved_blocks_without_request_truncate_oversized_display_text()
    {
        // The persisted ceiling (16 KB) is much larger than Slack's per-section
        // 2500-char cap. The builder must still truncate at render time so a
        // recovered journal with a giant body doesn't blow the section cap.
        var oversized = new string('x', 5_000);
        var blocks = SlackApprovalBlockBuilder.BuildResolvedApprovalBlocksWithoutRequest(
            ApprovalOptionKeys.ApproveSession,
            "U123",
            toolName: "shell_execute",
            displayText: oversized);

        foreach (var block in blocks.OfType<SectionBlock>())
        {
            if (block.Text is Markdown md)
                Assert.True(md.Text.Length < 3001,
                    $"SectionBlock text length {md.Text.Length} exceeded Slack's 3000-char cap");
        }
    }

    [Fact]
    public void Oversized_command_keeps_every_block_under_Slack_cap()
    {
        // Regression for the auto-deny-on-Slack-failure bug: a multi-KB
        // `gh issue create --body '...'` blew past Slack's 3000-char per
        // SectionBlock text cap and the API returned invalid_blocks. See
        // session D0AC6CKBK5K/1779811366.695739.
        var oversized = new string('x', 10_000);
        var request = Request(oversized, ["gh issue create"], "/home/user/repos/foo", FullButtonRow());

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);

        foreach (var block in blocks)
        {
            if (block is SectionBlock { Text: Markdown md })
                Assert.True(md.Text.Length < 3001, $"SectionBlock text length {md.Text.Length} exceeded Slack's 3000-char cap");
        }
    }
}
