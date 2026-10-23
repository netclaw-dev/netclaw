// -----------------------------------------------------------------------
// <copyright file="TextAnswerGuardMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// The text answer guard of the session (design D7 of <c>child-approval-events-and-parent-pause</c>, as the PR 2
/// review changed it). A text answer names no prompt. When a child prompt is among the prompts that the sender can
/// answer, the text resolves a prompt only when the sender can answer exactly one open prompt, so model text about one
/// command cannot approve another. With parent prompts only, the session keeps its rule from before PR 2: the text
/// resolves the latest prompt by its durable request time. Each test builds the approval table directly, with no
/// actor, no timer, and no delay. <c>PendingApprovalLookupTests</c> covers the channel lookup, which resolves the
/// earliest prompt.
/// </summary>
public sealed class TextAnswerGuardMutationTests
{
    private static readonly SessionId Session = new("test/text-answer-guard");

    [Fact]
    public void Two_parent_prompts_of_the_sender_resolve_the_latest()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(Request("call-2", "alice", 20), recovered: false);
        workflow.ApplyApprovalRequested(Request("call-1", "alice", 10), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("alice"), SessionState.Empty, out var response, out var nack);

        Assert.True(found);
        Assert.Null(nack);
        Assert.Equal("call-2", response!.CallId.Value);
    }

    [Fact]
    public void A_parent_prompt_and_a_child_prompt_of_the_sender_resolve_neither()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(Request("call-1", "alice", 10), recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("r-1", "alice", 20), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("alice"), SessionState.Empty, out var response, out var nack);

        Assert.False(found);
        Assert.Null(response);
        Assert.Equal(ApprovalNackReasons.UseButtons, nack);
    }

    [Fact]
    public void Two_child_prompts_of_the_sender_resolve_neither()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("r-1", "alice", 10), recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("r-2", "alice", 20), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("alice"), SessionState.Empty, out var response, out var nack);

        Assert.False(found);
        Assert.Null(response);
        Assert.Equal(ApprovalNackReasons.UseButtons, nack);
    }

    [Fact]
    public void One_prompt_of_the_sender_resolves_it()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(Request("call-1", "alice", 10), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("alice"), SessionState.Empty, out var response, out var nack);

        Assert.True(found);
        Assert.Null(nack);
        Assert.Equal("call-1", response!.CallId.Value);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, response.SelectedKey.Value);
    }

    [Fact]
    public void The_guard_counts_only_the_prompts_that_the_sender_can_answer()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(Request("call-alice", "alice", 10), recovered: false);
        workflow.ApplyApprovalRequested(Request("call-bob", "bob", 20), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("bob"), SessionState.Empty, out var response, out _);

        Assert.True(found);
        Assert.Equal("call-bob", response!.CallId.Value);
    }

    private static ToolInteractionTextResponse Text(string sender) => new()
    {
        SessionId = Session,
        Text = "A",
        SenderId = new SenderId(sender)
    };

    private static ToolApprovalRequested ChildRequest(string runId, string requester, long requestedAtMs)
        => Request(ChildApprovalPrompt.CreateCallId(runId, 1).Value, requester, requestedAtMs) with
        {
            SourceChildRunId = new SubAgentRunId(runId)
        };

    private static ToolApprovalRequested Request(string callId, string requester, long requestedAtMs)
    {
        var context = new TurnContext
        {
            SessionId = Session,
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ChannelType = ChannelType.Slack,
            RequesterSenderId = new SenderId(requester),
            RequesterPrincipal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            SupportsInteractiveApproval = true
        };

        return new ToolApprovalRequested
        {
            SessionId = Session,
            CallId = callId,
            AuthorizationAttemptId = $"auth-{callId}",
            ToolName = "shell_execute",
            Patterns = ["git status"],
            CandidateVerbs = ["git"],
            Audience = context.Audience,
            Boundary = context.Boundary,
            ChannelType = context.ChannelType?.ToWireValue(),
            SupportsInteractiveApproval = context.SupportsInteractiveApproval,
            RequesterSenderId = context.RequesterSenderId,
            RequesterPrincipal = context.RequesterPrincipal,
            Cwd = "/repo",
            OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            TurnContext = context.ToRecord(),
            RequestedAtMs = requestedAtMs
        };
    }
}
