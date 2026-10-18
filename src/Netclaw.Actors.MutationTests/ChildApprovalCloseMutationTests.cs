// -----------------------------------------------------------------------
// <copyright file="ChildApprovalCloseMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Issue 2525. The approval table of a session holds child approvals, and <c>AllowsSnapshot</c> needs an empty
/// table. A close event that misses an entry strands that entry, and every snapshot stays blocked. A close event
/// that removes too much drops a prompt that is still open, so a person cannot answer it. Each test builds the
/// table directly. No test uses an actor, a timer, or a delay.
/// </summary>
public sealed class ChildApprovalCloseMutationTests
{
    private static readonly SessionId Session = new("test/child-approval-close");

    [Fact]
    public void Closing_a_run_removes_its_pending_and_resolved_entries()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/2"), durable: true, recovered: false);
        workflow.ApplyResolvedResponse(Resolved("run-1/subagent-approval/2"));

        workflow.ApplyChildApprovalsClosed(Closed("run-1"));

        Assert.Equal(0, workflow.ChildApprovalCount(new SubAgentRunId("run-1")));
        Assert.False(workflow.IsChildApproval(new ToolCallId("run-1/subagent-approval/1")));
        Assert.False(workflow.IsChildApproval(new ToolCallId("run-1/subagent-approval/2")));
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void Closing_a_run_keeps_the_entries_of_another_run()
    {
        // The id "run-1" is a prefix of "run-10". A match on text would drop the second run.
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("run-10", "run-10/subagent-approval/1"), durable: true, recovered: false);

        workflow.ApplyChildApprovalsClosed(Closed("run-1"));

        Assert.True(workflow.IsChildApproval(new ToolCallId("run-10/subagent-approval/1")));
        Assert.Equal(1, workflow.ChildApprovalCount(new SubAgentRunId("run-10")));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void Closing_a_run_keeps_a_parent_approval()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ParentRequest("call-parent"), durable: true, recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);

        workflow.ApplyChildApprovalsClosed(Closed("run-1"));

        Assert.Equal(0, workflow.ChildApprovalCount(new SubAgentRunId("run-1")));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));
        Assert.Contains(workflow.OpenApprovals(), pending => pending.Request.CallId.Value == "call-parent");
        Assert.Single(workflow.OpenApprovals());
    }

    [Fact]
    public void Closing_a_run_with_no_entry_is_not_an_error_and_changes_nothing()
    {
        // Replay applies a close event after a snapshot or an earlier close removed the entries.
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-2", "run-2/subagent-approval/1"), durable: true, recovered: false);

        workflow.ApplyChildApprovalsClosed(Closed("run-unknown"));
        workflow.ApplyChildApprovalsClosed(Closed("run-2"));
        workflow.ApplyChildApprovalsClosed(Closed("run-2"));

        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void An_open_child_entry_blocks_a_snapshot_until_its_run_closes()
    {
        var workflow = new SessionToolWorkflow();
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));

        workflow.ApplyApprovalRequested(ChildRequest("run-3", "run-3/subagent-approval/1"), durable: true, recovered: false);
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));

        workflow.ApplyChildApprovalsClosed(Closed("run-3"));
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));
    }

    private static ChildApprovalsClosed Closed(string runId)
        => new()
        {
            SessionId = Session,
            RunId = new SubAgentRunId(runId),
            Reason = ChildApprovalCloseReason.RunCancelled,
            ClosedAtMs = 10
        };

    private static ToolApprovalResolved Resolved(string callId)
        => new()
        {
            SessionId = Session,
            CallId = callId,
            Decision = ConsentAnswerCodec.ToJournalText(ConsentAnswer.Once.Instance),
            ResolvedAtMs = 5
        };

    private static ToolApprovalRequested ChildRequest(string runId, string callId)
        => ParentRequest(callId) with
        {
            SourceChildRunId = new SubAgentRunId(runId),
            OriginalChildCallId = new ToolCallId("child-call-1")
        };

    private static ToolApprovalRequested ParentRequest(string callId)
    {
        var context = new TurnContext
        {
            SessionId = Session,
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ChannelType = ChannelType.Slack,
            RequesterSenderId = new SenderId("user-1"),
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
            RequestedAtMs = 1
        };
    }
}
