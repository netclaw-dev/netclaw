// -----------------------------------------------------------------------
// <copyright file="MattermostApprovalPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

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
        var toolName = new Netclaw.Tools.ToolName("shell_execute");
        Assert.Contains(ApprovalOptionKeys.ApproveOnceLabel, MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveOnce, toolName));
        Assert.Contains(ApprovalOptionKeys.ApproveAlwaysLabel, MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.ApproveAlways, toolName));
        Assert.Contains(ApprovalOptionKeys.DenyLabel, MattermostApprovalPromptBuilder.BuildDecisionStatus(ApprovalOptionKeys.Deny, toolName));
    }

    [Fact]
    public void BuildDecisionStatus_uses_MCP_persistent_label()
    {
        var status = MattermostApprovalPromptBuilder.BuildDecisionStatus(
            ApprovalOptionKeys.ApproveEverywhere,
            new Netclaw.Tools.ToolName("Dropbox/upload"));

        Assert.Contains(ApprovalOptionKeys.ApproveMcpToolLabel, status);
        Assert.DoesNotContain(ApprovalOptionKeys.ApproveEverywhereLabel, status);
    }

    [Fact]
    public void BuildDecisionStatus_passes_through_unknown_key()
    {
        var status = MattermostApprovalPromptBuilder.BuildDecisionStatus(
            "custom_key",
            new Netclaw.Tools.ToolName("shell_execute"));
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
            request, "http://localhost:5199/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1");

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
            request, "http://callback:5199/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1", actionStore);

        var approveOnce = attachments[0].Actions![0];
        Assert.Equal("toolapproval0", approveOnce.Id);
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
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1");

        var denyButton = attachments[0].Actions!.Single(a => a.Name == ApprovalOptionKeys.DenyLabel);
        Assert.Equal("danger", denyButton.Style);
    }

    [Fact]
    public void BuildButtonPrompt_assignment_everywhere_button_has_danger_style()
    {
        var request = CreateStandardRequest() with
        {
            Options =
            [
                new ToolInteractionOption(
                    ApprovalOptionKeys.ApproveAssignmentEverywhereV1Key,
                    ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel),
            ],
        };

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request,
            "http://localhost/api/mattermost/actions",
            "ch-1",
            "root-post-1",
            "prompt-corr-1");

        var everywhere = attachments[0].Actions!.Single(action =>
            action.Name == ApprovalOptionKeys.ApproveEverywhereLabel);
        Assert.Equal("danger", everywhere.Style);
    }

    [Fact]
    public void BuildButtonPrompt_approve_once_has_primary_style()
    {
        var request = CreateStandardRequest();

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1");

        var approveOnce = attachments[0].Actions!.Single(a => a.Name == ApprovalOptionKeys.ApproveOnceLabel);
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
    public void BuildResolvedAttachmentWithoutRequest_includes_persisted_tool_name_and_display_text()
    {
        var attachment = MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
            ApprovalOptionKeys.ApproveSession,
            "user-99",
            toolName: "shell_execute",
            displayText: "gh pr create --base master --head feature/foo");

        Assert.Contains("**Tool:** `shell_execute`", attachment.Text!);
        Assert.Contains("gh pr create --base master --head feature/foo", attachment.Text!);
        Assert.Null(attachment.Actions);
    }

    [Fact]
    public void BuildResolvedAttachmentWithoutRequest_falls_back_to_generic_when_only_one_field_supplied()
    {
        var toolOnly = MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "user-99", toolName: "shell_execute", displayText: null);
        Assert.DoesNotContain("**Tool:**", toolOnly.Text!);

        var displayOnly = MattermostApprovalPromptBuilder.BuildResolvedAttachmentWithoutRequest(
            ApprovalOptionKeys.ApproveOnce, "user-99", toolName: null, displayText: "gh pr create");
        Assert.DoesNotContain("gh pr create", displayOnly.Text!);
    }

    [Fact]
    public void BuildButtonPrompt_with_action_store_includes_action_token()
    {
        var request = CreateStandardRequest();
        var actionStore = new MattermostCallbackActionStore(TimeProvider.System);

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1", actionStore);

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
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1");

        foreach (var action in attachments[0].Actions!)
        {
            Assert.False(action.Context.ContainsKey("action_token"), $"Button '{action.Id}' should not have action token");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildButtonPrompt_routes_alphanumeric_ids_and_preserves_one_time_option_tokens(bool versionedOption)
    {
        var request = CreateStandardRequest();
        if (versionedOption)
        {
            request = request with
            {
                Options =
                [
                    new ToolInteractionOption(
                        ApprovalOptionKeys.ApproveAssignmentEverywhereV1Key,
                        ApprovalOptionKeys.ApproveEverywhereLabel),
                    new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]
            };
        }
        var actionStore = new MattermostCallbackActionStore(TimeProvider.System);

        var (_, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "http://localhost/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1", actionStore);

        var actions = attachments[0].Actions!;
        Assert.Equal(request.Options.Count, actions.Select(action => action.Id).Distinct().Count());
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            Assert.Matches("^[A-Za-z0-9]+$", action.Id);
            Assert.True(actionStore.TryConsume(action.Context["action_token"], out var stored));
            Assert.NotNull(stored);
            Assert.Equal("ch-1", stored!.ChannelId);
            Assert.Equal("call-btn-1", stored.CallId);
            Assert.Equal(request.Options[index].Key.Value, stored.SelectedKey);
            Assert.Equal("root-post-1", stored.RootPostId);
            Assert.Equal("requester-1", stored.RequesterSenderId);
            Assert.False(actionStore.TryConsume(action.Context["action_token"], out _));
        }
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
    public void Mcp_prompt_renders_invocation_without_shell_patterns()
    {
        var request = CreateStandardRequest() with
        {
            ToolName = new Netclaw.Tools.ToolName("Dropbox/upload"),
            DisplayText = "Dropbox/upload(destination_directory=\"/Finance/Q3\", contents=(90000 chars, 2000 lines))",
            Patterns = ["Dropbox/upload"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveMcpToolLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var text = MattermostApprovalPromptBuilder.BuildTextPrompt(request);

        Assert.Contains("MCP tool approval required", text);
        Assert.Contains("**Invocation:**", text);
        Assert.Contains("Allow this MCP tool invocation?", text);
        Assert.Contains(ApprovalOptionKeys.ApproveMcpToolLabel, text);
        Assert.DoesNotContain("**Pattern", text);
        Assert.DoesNotContain("Always anywhere", text);
    }

    [Fact]
    public void Mcp_resolution_uses_contextual_persistent_grant_label()
    {
        var request = CreateStandardRequest() with
        {
            ToolName = new Netclaw.Tools.ToolName("Dropbox/upload"),
            DisplayText = "Dropbox/upload(path=\"/Finance/Q3\")"
        };

        var text = MattermostApprovalPromptBuilder.BuildResolvedPromptText(
            request,
            ApprovalOptionKeys.ApproveEverywhere,
            "user-1");

        Assert.Contains("MCP tool approval resolved", text);
        Assert.Contains($"**Decision:** {ApprovalOptionKeys.ApproveMcpToolLabel}", text);
        Assert.DoesNotContain("Allow this MCP tool invocation?", text);
        Assert.DoesNotContain("Always anywhere", text);
    }

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
            request, "https://callback.example/url", channelId: "ch-1", rootPostId: "root-1", promptCorrelationId: "prompt-corr-1");

        Assert.True(textPrompt.Length < 16_001, $"Text prompt length {textPrompt.Length} exceeded Mattermost's 16000-char cap");
        Assert.True(buttonText.Length < 16_001, $"Button prompt length {buttonText.Length} exceeded Mattermost's 16000-char cap");
    }

    // A pattern can be the full text of one command. With a long display text,
    // unbounded patterns push the post over the Mattermost cap.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_command_patterns_keep_the_prompt_postable(bool withSiblingVerbs)
    {
        var baseRequest = LongApprovalCommand.Request(withSiblingVerbs);
        var longPattern = baseRequest.Patterns.Select(static p => p + new string('x', 12_000)).ToArray();
        var request = baseRequest with
        {
            DisplayText = baseRequest.DisplayText + new string('y', 20_000),
            Patterns = longPattern,
            CandidateVerbs = longPattern
        };

        var textPrompt = MattermostApprovalPromptBuilder.BuildTextPrompt(request);
        var (buttonText, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "https://callback.example/url", channelId: "ch-1", rootPostId: "root-1", promptCorrelationId: "prompt-corr-1");
        var resolved = MattermostApprovalPromptBuilder.BuildResolvedPromptText(request, ApprovalOptionKeys.Deny, "U123");

        Assert.All([textPrompt, buttonText, resolved], message => Assert.InRange(message.Length, 1, 16_000));
        Assert.Equal(request.Options.Select(static o => o.Label), Assert.Single(attachments).Actions!.Select(static a => a.Name));
        Assert.Contains("**B)** Deny", textPrompt, StringComparison.Ordinal);
        Assert.Contains("characters hidden", buttonText, StringComparison.Ordinal);
        Assert.Contains(withSiblingVerbs ? "  - `git push" : "**Pattern:** `gh api repos/netclaw-dev", buttonText, StringComparison.Ordinal);
    }

    // PR 2 of child-approval-events-and-parent-pause (design D3, S7; TA-12; channel-binding-parity "Child prompts have
    // buttons only"). A child prompt shows the command and the scope with buttons only: no "reply with" text and no
    // child identity. The request of a child prompt has the call ID that the bridge makes, "{runId}/subagent-approval/{n}",
    // and no agent name. A parent prompt keeps its text reply hint.
    // Planted bug: the builder adds "You can also reply with A, B" to every prompt (today), or it prints the call ID.
    private const string ChildRunId = "0f3c9a7d5e2b41c8a6d4e1f2b3c4d5e6";

    private static ToolInteractionRequest ChildPrompt(bool child) => new()
    {
        SessionId = new SessionId("test/child-prompt"),
        Kind = "approval",
        CallId = new Netclaw.Tools.ToolCallId(child ? $"{ChildRunId}/subagent-approval/1" : "call-parent-1"),
        ToolName = new Netclaw.Tools.ToolName("shell_execute"),
        DisplayText = "gh pr view 2558",
        RequesterSenderId = new SenderId("user-1"),
        Lifetime = child ? ToolInteractionLifetime.UntilClosed : ToolInteractionLifetime.UntilTurnEnd,
        Patterns = ["gh pr view"],
        CandidateVerbs = ["gh pr view"],
        Cwd = "/repo",
        Options =
        [
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Child_prompt_has_buttons_only_and_no_child_identity(bool child)
    {
        var request = ChildPrompt(child);

        var (text, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "https://example.test/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1");
        var visible = string.Join('\n', new[] { text }.Concat(attachments.SelectMany(attachment =>
            new[] { attachment.Text ?? string.Empty, attachment.Fallback ?? string.Empty }
                .Concat((attachment.Actions ?? []).Select(action => action.Name)))));

        Assert.Contains("gh pr view 2558", visible, StringComparison.Ordinal);
        Assert.Contains(attachments, attachment => attachment.Actions is { Count: > 0 });
        Assert.DoesNotContain(ChildRunId, visible, StringComparison.Ordinal);
        Assert.Equal(!child, visible.Contains("reply with", StringComparison.OrdinalIgnoreCase));
    }

    // Review fix S1 of PR 2: the typed Lifetime is the one signal for a child prompt. A parent prompt whose call ID holds
    // "/subagent-approval/" keeps the lifetime "until turn end", so the builder draws it as a parent prompt with its reply
    // hint, in the message and in the attachment fallback.
    // Planted bug: the builder detects a child prompt by the call ID shape (today) and drops the reply hint.
    [Fact]
    public void Parent_prompt_with_a_child_shaped_call_id_keeps_its_reply_hint()
    {
        var request = ChildPrompt(child: true) with { Lifetime = ToolInteractionLifetime.UntilTurnEnd };

        var (text, attachments) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "https://example.test/api/mattermost/actions", "ch-1", "root-post-1", "prompt-corr-1");

        Assert.Contains("You can also reply with", text, StringComparison.Ordinal);
        var attachment = Assert.Single(attachments);
        Assert.Contains("reply with", attachment.Fallback ?? string.Empty, StringComparison.Ordinal);
        Assert.True(attachment.Actions is { Count: > 0 });
    }

    private const string ScopeNote = "Reusable grants cover the command words shown above within the selected scope.";

    private static ToolInteractionRequest ShellRequestWithNotes()
        => new()
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-notes"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = "git push origin main",
            Notes = ScopeNote,
            Patterns = ["git push origin main"],
            CandidateVerbs = ["git push origin main"],
            Options = [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

    [Fact]
    public void Button_prompt_keeps_the_command_in_its_own_code_span_and_the_notes_outside_it()
    {
        var (text, _) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            ShellRequestWithNotes(), "https://cb", "chan", "root", "corr");

        Assert.Contains(
            "**Action:** `git push origin main`\n" + ScopeNote + "\n**Pattern:** `git push origin main`",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Text_prompt_keeps_the_command_in_its_own_code_span_and_the_notes_outside_it()
    {
        var text = MattermostApprovalPromptBuilder.BuildTextPrompt(ShellRequestWithNotes());

        Assert.Contains(
            "**Action:** `git push origin main`\n" + ScopeNote + "\n**Pattern:** `git push origin main`",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Resolved_post_shows_the_command_without_the_notes()
    {
        var text = MattermostApprovalPromptBuilder.BuildResolvedPromptText(
            ShellRequestWithNotes(), ApprovalOptionKeys.ApproveOnce, "alice");

        Assert.Contains("**Action:** `git push origin main`", text);
        Assert.DoesNotContain(ScopeNote, text);
    }
}
