// -----------------------------------------------------------------------
// <copyright file="TextAnswerParseOrderMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// The edges of the session text answer guard (design D7 of <c>child-approval-events-and-parent-pause</c>, review fix
/// N6). The guard needs two or more answerable prompts with a child prompt among them. The session parses the text
/// before it chooses the reply: text that answers no prompt gets the option-unavailable nack, as the channel binding
/// treats it, and text that answers at least one prompt gets the use-buttons nack. The channel lookup
/// (<see cref="PendingApprovalLookup"/>) has the same edge for one child prompt. Each test builds its table directly,
/// with no actor, no timer, and no delay.
/// </summary>
public sealed class TextAnswerParseOrderMutationTests
{
    private static readonly SessionId Session = new("test/text-answer-parse-order");

    // Planted bug: the guard counts one prompt as many, so a single child prompt cannot take a text answer.
    [Fact]
    public void One_child_prompt_of_the_sender_resolves_it()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("r-1", 10, TwoOptions), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("A"), SessionState.Empty, out var response, out var nack);

        Assert.True(found);
        Assert.Null(nack);
        Assert.Equal(ChildApprovalPrompt.CreateCallId("r-1", 1).Value, response!.CallId.Value);
    }

    // Planted bug: the guard answers "use the buttons" before it parses the text, so ordinary text such as "hello"
    // gets a misleading reply.
    [Fact]
    public void Text_that_answers_no_prompt_gets_the_option_unavailable_nack()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ParentRequest("call-parent", 10, TwoOptions), recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("r-1", 20, TwoOptions), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("hello"), SessionState.Empty, out var response, out var nack);

        Assert.False(found);
        Assert.Null(response);
        Assert.Equal(ApprovalNackReasons.OptionUnavailable, nack);
    }

    // "C" is an option of the parent prompt only. The text answers one prompt, so the reply is "use the buttons".
    // Planted bug: the guard asks that the text answers every prompt.
    [Fact]
    public void Text_that_answers_one_of_the_prompts_gets_the_use_buttons_nack()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ParentRequest("call-parent", 10, ThreeOptions), recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("r-1", 20, TwoOptions), recovered: false);

        var found = workflow.TryResolveTextResponse(Text("C"), SessionState.Empty, out var response, out var nack);

        Assert.False(found);
        Assert.Null(response);
        Assert.Equal(ApprovalNackReasons.UseButtons, nack);
    }

    // The channel lookup has the same edge: one child prompt of the sender takes a text answer.
    // Planted bug: the lookup counts one prompt as many, so a lone child prompt is ambiguous.
    [Fact]
    public void One_child_prompt_in_the_channel_lookup_matches()
    {
        var child = new PendingApprovalRequest<int>(new ToolInteractionRequest
        {
            SessionId = Session,
            Kind = "approval",
            CallId = ChildApprovalPrompt.CreateCallId("r-1", 1),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "gh pr view 12",
            RequesterSenderId = new SenderId("alice"),
            Lifetime = ToolInteractionLifetime.UntilClosed,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        });

        var (result, match) = PendingApprovalLookup.Resolve<PendingApprovalRequest<int>, int>([child], "alice", callId: null);

        Assert.Equal(ApprovalLookupResult.Matched, result);
        Assert.Same(child, match);
    }

    private static readonly string[] TwoOptions = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny];

    private static readonly string[] ThreeOptions =
        [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.Deny];

    private static ToolInteractionTextResponse Text(string text) => new()
    {
        SessionId = Session,
        Text = text,
        SenderId = new SenderId("alice")
    };

    private static ToolApprovalRequested ChildRequest(string runId, long requestedAtMs, string[] options)
        => ParentRequest(ChildApprovalPrompt.CreateCallId(runId, 1).Value, requestedAtMs, options) with
        {
            SourceChildRunId = new SubAgentRunId(runId)
        };

    private static ToolApprovalRequested ParentRequest(string callId, long requestedAtMs, string[] options)
    {
        var context = new TurnContext
        {
            SessionId = Session,
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ChannelType = ChannelType.Slack,
            RequesterSenderId = new SenderId("alice"),
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
            OptionKeys = options,
            TurnContext = context.ToRecord(),
            RequestedAtMs = requestedAtMs
        };
    }
}
