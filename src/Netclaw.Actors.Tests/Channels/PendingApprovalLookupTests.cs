// -----------------------------------------------------------------------
// <copyright file="PendingApprovalLookupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// The text answer guard in the shared channel lookup (PR 2 of the change <c>child-approval-events-and-parent-pause</c>,
/// design D7, task 4.3, as the PR 2 review changed it). A text answer has no call ID. When a child prompt
/// (<see cref="ToolInteractionLifetime.UntilClosed"/>) is among the prompts that the sender can answer, the text answer
/// resolves a prompt only when the sender can answer exactly one prompt. With parent prompts only, the text answer
/// resolves the earliest prompt of the sender. A button answer names its call ID, so the guard does not apply to it.
/// </summary>
public sealed class PendingApprovalLookupTests
{
    private const string ChildCallId = "0f3c9a7d5e2b41c8a6d4e1f2b3c4d5e6/subagent-approval/1";

    // Review fix B2: text-only Mattermost (no CallbackUrl) has no buttons, so the earliest-first rule is its only way to
    // answer parallel parent calls (channel-binding-parity "Text approval resolves the earliest pending approval").
    // Planted bug: the lookup is ambiguous for every set of two or more prompts (PR 2 as reviewed).
    [Fact]
    public void A_text_answer_with_two_parent_prompts_of_the_sender_resolves_the_earliest()
    {
        var pending = new List<PendingApprovalRequest<int>> { Pending("call-1", "alice"), Pending("call-2", "alice") };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Matched, result);
        Assert.Equal("call-1", match!.CallId.Value);
    }

    // Guard (passes today). A child prompt does not name the child, so a text answer resolves none when a child prompt
    // is among the prompts of the sender. The order of the list does not matter.
    // Planted bug: the guard checks only the first or only the last prompt, or it counts only child prompts.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_text_answer_with_a_parent_prompt_and_a_child_prompt_of_the_sender_is_ambiguous(bool childFirst)
    {
        var parent = Pending("call-parent", "alice");
        var child = Pending(ChildCallId, "alice", ToolInteractionLifetime.UntilClosed);
        var pending = childFirst
            ? new List<PendingApprovalRequest<int>> { child, parent }
            : new List<PendingApprovalRequest<int>> { parent, child };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Ambiguous, result);
        Assert.Null(match);
    }

    // Guard (passes today). Two child prompts of the sender stay ambiguous.
    [Fact]
    public void A_text_answer_with_two_child_prompts_of_the_sender_is_ambiguous()
    {
        var pending = new List<PendingApprovalRequest<int>>
        {
            Pending(ChildCallId, "alice", ToolInteractionLifetime.UntilClosed),
            Pending("0f3c9a7d5e2b41c8a6d4e1f2b3c4d5e6/subagent-approval/2", "alice", ToolInteractionLifetime.UntilClosed)
        };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Ambiguous, result);
        Assert.Null(match);
    }

    // Review fix B1. A cold-spawned binding restores a child prompt from PendingApprovalPromptTracked, with no
    // ToolInteractionRequest. The journaled lifetime makes it a child prompt, so a live parent prompt does not make the
    // text answer resolve the restored prompt.
    // Planted bug: the guard reads the lifetime from the request only, so a restored child prompt counts as a parent.
    [Fact]
    public void A_text_answer_with_a_restored_child_prompt_and_a_live_parent_prompt_is_ambiguous()
    {
        var restored = new PendingApprovalRequest<int>(
            new ToolCallId(ChildCallId),
            requesterSenderId: "alice",
            requesterPrincipal: null,
            optionKeys: [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            promptId: 7,
            lifetime: ToolInteractionLifetime.UntilClosed,
            toolName: "shell_execute",
            displayText: "gh pr view 2558");
        var pending = new List<PendingApprovalRequest<int>> { restored, Pending("call-live-parent", "alice") };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Ambiguous, result);
        Assert.Null(match);
    }

    // The guard counts only the prompts that the sender can answer. Bob's child prompt does not stop Alice's text answer
    // to her earliest parent prompt.
    // Planted bug: the guard counts every child prompt in the list, also one that the sender cannot answer.
    [Fact]
    public void A_child_prompt_that_the_sender_cannot_answer_does_not_block_the_parent_rule()
    {
        var pending = new List<PendingApprovalRequest<int>>
        {
            Pending(ChildCallId, "bob", ToolInteractionLifetime.UntilClosed),
            Pending("call-parent-1", "alice"),
            Pending("call-parent-2", "alice")
        };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Matched, result);
        Assert.Equal("call-parent-1", match!.CallId.Value);
    }

    // Review fix S1: the typed lifetime is the one signal for a child prompt. A parent prompt whose call ID holds
    // "/subagent-approval/" is a parent prompt, so two of them resolve the earliest.
    // Planted bug: the lookup detects a child prompt by the call ID shape.
    [Fact]
    public void A_parent_prompt_with_a_child_shaped_call_id_counts_as_a_parent_prompt()
    {
        var pending = new List<PendingApprovalRequest<int>>
        {
            Pending(ChildCallId, "alice"),
            Pending("call-parent-2", "alice")
        };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Matched, result);
        Assert.Equal(ChildCallId, match!.CallId.Value);
    }

    // Guard (passes today). The guard counts only the prompts that the sender can answer. Alice can answer only the
    // earliest prompt (channel-binding-parity "Text approval resolves the earliest pending approval").
    [Fact]
    public void A_text_answer_resolves_the_one_prompt_that_the_sender_can_answer()
    {
        var pending = new List<PendingApprovalRequest<int>> { Pending("call-alice", "alice"), Pending("call-bob", "bob") };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(pending, "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Matched, result);
        Assert.Equal("call-alice", match!.CallId.Value);
    }

    // Guard (passes today). A button names its call ID, so two pending prompts do not make it ambiguous.
    [Fact]
    public void A_button_answer_with_two_prompts_of_the_sender_resolves_its_own_call()
    {
        var pending = new List<PendingApprovalRequest<int>>
        {
            Pending(ChildCallId, "alice", ToolInteractionLifetime.UntilClosed),
            Pending("call-2", "alice")
        };

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>(
            pending, "alice", new ToolCallId("call-2"));

        Assert.Equal(ApprovalLookupResult.Matched, result);
        Assert.Equal("call-2", match!.CallId.Value);
    }

    private static PendingApprovalRequest<int> Pending(
        string callId,
        string requester,
        ToolInteractionLifetime lifetime = ToolInteractionLifetime.UntilTurnEnd)
        => new(new ToolInteractionRequest
        {
            SessionId = new SessionId("test/lookup"),
            Kind = "approval",
            CallId = new ToolCallId(callId),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "git status",
            RequesterSenderId = new SenderId(requester),
            Lifetime = lifetime,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        });
}
