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

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackApprovalBlockBuilderTests
{
    private static IReadOnlyList<ToolInteractionOption> FullButtonRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhere, ApprovalOptionKeys.ApproveEverywhereLabel),
        new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
    ];

    private static IReadOnlyList<ToolInteractionOption> MessyRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
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
}
