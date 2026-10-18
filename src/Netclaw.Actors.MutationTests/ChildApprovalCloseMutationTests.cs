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
/// table. One rule closes a child approval: the cancel record or the terminal record of its run closes every entry of
/// that run. Live code and journal replay both use it. A rule that misses an entry strands it, and every snapshot stays
/// blocked. A rule that removes too much drops a prompt that is still open, so a person cannot answer it. Each test
/// builds the table directly. No test uses an actor, a timer, or a delay.
/// </summary>
public sealed class ChildApprovalCloseMutationTests
{
    private static readonly SessionId Session = new("test/child-approval-close");

    [Fact]
    public void A_cancel_record_closes_the_pending_and_resolved_entries_of_its_run()
    {
        var workflow = TwoEntriesOf("run-1");

        workflow.CloseChildApprovals(Cancel("run-1"));

        AssertRunClosed(workflow, "run-1");
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void A_terminal_record_closes_the_entries_of_its_run()
    {
        var workflow = TwoEntriesOf("run-1");

        workflow.CloseChildApprovals(Terminal("run-1", lost: false));

        AssertRunClosed(workflow, "run-1");
    }

    [Fact]
    public void A_lost_terminal_record_closes_the_entries_of_its_run()
    {
        // Recovery writes this record when it marks an open child run lost.
        var workflow = TwoEntriesOf("run-1");

        workflow.CloseChildApprovals(Terminal("run-1", lost: true));

        AssertRunClosed(workflow, "run-1");
    }

    [Fact]
    public void A_checkpoint_or_a_prepared_result_closes_nothing()
    {
        // The run is still live after a checkpoint, and its prompt stays open. A prepared result follows a terminal record.
        var workflow = TwoEntriesOf("run-1");

        workflow.CloseChildApprovals(new ChildRunEvent.Checkpointed(
            new ChildRunCheckpoint(1, "round one", new WorkingContextDelta()))
        { SessionId = Session, RunId = new SubAgentRunId("run-1"), RecordedAtMs = 20 });
        workflow.CloseChildApprovals(new ChildRunEvent.ResultPrepared(
            new SerializableChatMessage(), new SerializableChatMessage(), null)
        { SessionId = Session, RunId = new SubAgentRunId("run-1"), RecordedAtMs = 21 });

        Assert.Equal(1, workflow.ChildApprovalCount(new SubAgentRunId("run-1")));
        Assert.True(workflow.IsChildApproval(new ToolCallId("run-1/subagent-approval/1")));
        Assert.True(workflow.IsChildApproval(new ToolCallId("run-1/subagent-approval/2")));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void A_record_closes_only_the_entries_of_its_own_run()
    {
        // The id "run-1" is a prefix of "run-10". A match on text would drop the second run.
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("run-10", "run-10/subagent-approval/1"), durable: true, recovered: false);

        workflow.CloseChildApprovals(Cancel("run-1"));

        Assert.True(workflow.IsChildApproval(new ToolCallId("run-10/subagent-approval/1")));
        Assert.Equal(1, workflow.ChildApprovalCount(new SubAgentRunId("run-10")));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void A_record_keeps_a_parent_approval()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ParentRequest("call-parent"), durable: true, recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);

        workflow.CloseChildApprovals(Terminal("run-1", lost: false));

        Assert.Equal(0, workflow.ChildApprovalCount(new SubAgentRunId("run-1")));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void A_record_for_a_run_with_no_entry_changes_nothing()
    {
        // Replay applies the record of a run whose entries a snapshot or an earlier record already removed.
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-2", "run-2/subagent-approval/1"), durable: true, recovered: false);

        workflow.CloseChildApprovals(Cancel("run-unknown"));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));

        workflow.CloseChildApprovals(Cancel("run-2"));
        workflow.CloseChildApprovals(Terminal("run-2", lost: true));
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void Recovery_closes_every_child_entry_and_keeps_a_parent_entry()
    {
        // A child owner never survives a restart, so no child entry stays open when replay ends.
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ParentRequest("call-parent"), durable: true, recovered: true);
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: true);
        workflow.ApplyApprovalRequested(ChildRequest("run-10", "run-10/subagent-approval/1"), durable: true, recovered: true);

        workflow.ExpireRecoveredChildApprovals();

        Assert.Equal(0, workflow.ChildApprovalCount(new SubAgentRunId("run-1")));
        Assert.Equal(0, workflow.ChildApprovalCount(new SubAgentRunId("run-10")));
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));
    }

    [Fact]
    public void A_table_with_no_entry_allows_a_snapshot()
    {
        var workflow = new SessionToolWorkflow();
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));

        workflow.ApplyApprovalRequested(ChildRequest("run-3", "run-3/subagent-approval/1"), durable: true, recovered: false);
        Assert.False(workflow.AllowsSnapshot(SessionState.Empty));

        workflow.ExpireRecoveredChildApprovals();
        Assert.True(workflow.AllowsSnapshot(SessionState.Empty));
    }

    private static SessionToolWorkflow TwoEntriesOf(string runId)
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest(runId, $"{runId}/subagent-approval/1"), durable: true, recovered: false);
        workflow.ApplyApprovalRequested(ChildRequest(runId, $"{runId}/subagent-approval/2"), durable: true, recovered: false);
        workflow.ApplyResolvedResponse(Resolved($"{runId}/subagent-approval/2"));
        return workflow;
    }

    private static void AssertRunClosed(SessionToolWorkflow workflow, string runId)
    {
        Assert.Equal(0, workflow.ChildApprovalCount(new SubAgentRunId(runId)));
        Assert.False(workflow.IsChildApproval(new ToolCallId($"{runId}/subagent-approval/1")));
        Assert.False(workflow.IsChildApproval(new ToolCallId($"{runId}/subagent-approval/2")));
    }

    private static ChildRunEvent.CancellationRequested Cancel(string runId)
        => new() { SessionId = Session, RunId = new SubAgentRunId(runId), RecordedAtMs = 10 };

    private static ChildRunEvent.TerminalRecorded Terminal(string runId, bool lost)
        => new(new ChildRunTerminal(new SubAgentProtocol.SubAgentResult
        {
            AgentName = new AgentName("worker"),
            RunId = new SubAgentRunId(runId),
            Output = "Done.",
            Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.ActorStopped)
        }, lost, null))
        { SessionId = Session, RunId = new SubAgentRunId(runId), RecordedAtMs = 11 };

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
