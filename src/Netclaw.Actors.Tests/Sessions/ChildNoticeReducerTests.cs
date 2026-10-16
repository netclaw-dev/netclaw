// -----------------------------------------------------------------------
// <copyright file="ChildNoticeReducerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Own specs of the S2 implementation, not part of the specs that the test author wrote. They state the rules of the
/// reducer for a child result notice, and the rule of the plan that decides what a new input does to an open approval.
/// </summary>
public sealed class ChildNoticeReducerTests
{
    private static readonly SessionId Owner = new("test/notice-reducer");

    /// <summary>
    /// The notice turns the ended run into a reported run in the same step that it admits the input. The reported run keeps
    /// the outcome, the reason, and the notice input. The reviewed context joins the session working context once.
    /// Planted bug: the reducer leaves the run in the terminal state, or forgets the reviewed context.
    /// </summary>
    [Fact]
    public void A_notice_reports_the_ended_run_and_applies_its_reviewed_context_once()
    {
        // Arrange
        var (state, run) = EndedRun();
        var delta = new WorkingContextDelta { ConfirmedChangedFiles = ["/repo/changed.cs"] };

        // Act
        var admitted = state.Apply(Notice(run, delta, "notice-1"));

        // Assert
        var reported = Assert.IsType<ChildRunLifecycle.Reported>(admitted.ChildRuns[run.RunId].Lifecycle);
        Assert.Equal(SubAgentRunOutcome.Completed, reported.Outcome);
        Assert.Null(reported.Reason);
        Assert.Equal(new ChildRunReport.Notice(new InputId("notice-1")), reported.Report);
        Assert.Null(admitted.ChildRuns[run.RunId].InitialWorkingSnapshot);
        Assert.Equal(["/repo/changed.cs"], admitted.WorkingContext.RecentFiles);
        Assert.Equal(new InputId("notice-1"), Assert.Single(admitted.PendingInputs).InputId);
        Assert.Empty(admitted.History);
    }

    /// <summary>
    /// A run gets one notice. The state of the run is the guard, so a second notice fails before any write, also after the
    /// recent source keys of the session are gone.
    /// Planted bug: the guard reads the recent source keys.
    /// </summary>
    [Fact]
    public void A_second_notice_for_the_same_run_is_refused()
    {
        // Arrange
        var (state, run) = EndedRun();
        var admitted = state.Apply(Notice(run, delta: null, "notice-1")) with
        {
            RecentSourceMessageKeys = []
        };

        // Act and Assert
        var error = Assert.Throws<InvalidOperationException>(() => admitted.Apply(Notice(run, delta: null, "notice-2")));
        Assert.Contains("requires an ended run", error.Message, StringComparison.Ordinal);
        Assert.Single(admitted.PendingInputs);
    }

    /// <summary>
    /// A run that still runs has no result to report.
    /// Planted bug: the reducer accepts a notice for a run in any state.
    /// </summary>
    [Fact]
    public void A_notice_for_a_running_child_is_refused()
    {
        // Arrange
        var run = Run();
        var state = SessionState.Empty.Apply(new ChildRunAccepted { SessionId = Owner, Run = run, RecordedAtMs = 1 });

        // Act and Assert
        var error = Assert.Throws<InvalidOperationException>(() => state.Apply(Notice(run, delta: null, "notice-1")));
        Assert.Contains("requires an ended run", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice carries the recorded authority of the run owner. A notice for another requester is refused, so a
    /// notice can never run a turn under wider or other authority.
    /// Planted bug: the reducer does not compare the authority.
    /// </summary>
    [Fact]
    public void A_notice_with_the_authority_of_another_requester_is_refused()
    {
        // Arrange
        var (state, run) = EndedRun();
        var other = Notice(run, delta: null, "notice-1") with
        {
            TurnContext = run.OriginalContext with { RequesterSenderId = new SenderId("mallory") }
        };

        // Act and Assert
        var error = Assert.Throws<InvalidOperationException>(() => state.Apply(other));
        Assert.Contains("recorded authority", error.Message, StringComparison.Ordinal);
        Assert.IsType<ChildRunLifecycle.Terminal>(state.ChildRuns[run.RunId].Lifecycle);
    }

    /// <summary>
    /// A notice is a user input. The reducer refuses any other role, so a notice cannot add a tool or system message.
    /// Planted bug: the reducer does not check the role of the notice message.
    /// </summary>
    [Theory]
    [InlineData(ChatRole.Assistant)]
    [InlineData(ChatRole.System)]
    [InlineData(ChatRole.Tool)]
    public void A_notice_that_is_not_a_user_message_is_refused(ChatRole role)
    {
        // Arrange
        var (state, run) = EndedRun();
        var notice = Notice(run, delta: null, "notice-1");

        // Act and Assert
        var error = Assert.Throws<InvalidOperationException>(() => state.Apply(notice with
        {
            UserMessage = notice.UserMessage with { Role = role }
        }));
        Assert.Contains("recorded authority", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A notice names a run that the session accepted. A notice for an unknown run, or for a run of another session, is refused.
    /// Planted bug: the reducer accepts a notice for any run id.
    /// </summary>
    [Fact]
    public void A_notice_for_an_unknown_run_or_another_session_is_refused()
    {
        // Arrange
        var (state, run) = EndedRun();

        // Act and Assert
        var unknown = Assert.Throws<InvalidOperationException>(() => state.Apply(Notice(run, delta: null, "notice-1") with
        {
            ChildNotice = new ChildNotice(new SubAgentRunId("unknown-run"), null)
        }));
        var other = Assert.Throws<InvalidOperationException>(() => state.Apply(Notice(run, delta: null, "notice-2") with
        {
            SessionId = new SessionId("test/another-session")
        }));
        Assert.Contains("does not name an accepted run", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("does not name an accepted run", other.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a successful run has a reviewed context to apply.
    /// Planted bug: the reducer applies a context of a failed run.
    /// </summary>
    [Fact]
    public void A_notice_of_a_failed_run_cannot_carry_a_reviewed_context()
    {
        // Arrange
        var (state, run) = EndedRun(success: false);

        // Act and Assert
        var error = Assert.Throws<InvalidOperationException>(() => state.Apply(Notice(run, new WorkingContextDelta(), "notice-1")));
        Assert.Contains("reviewed working context", error.Message, StringComparison.Ordinal);
        var admitted = state.Apply(Notice(run, delta: null, "notice-1"));
        var reported = Assert.IsType<ChildRunLifecycle.Reported>(admitted.ChildRuns[run.RunId].Lifecycle);
        Assert.Equal(SubAgentRunOutcome.Failed, reported.Outcome);
        Assert.Equal(SubAgentOutcomeReason.ToolExecutionFailed, reported.Reason);
    }

    /// <summary>
    /// A person's notice must not abandon the open approval of that person. The plan classifies the input by its kind, and
    /// the principal of a notice is the principal of the child owner, which is a person.
    /// Planted bug: the plan classifies the notice by its principal only.
    /// </summary>
    [Theory]
    [InlineData(PrincipalClassification.Operator)]
    [InlineData(PrincipalClassification.TrustedInternal)]
    [InlineData(PrincipalClassification.UntrustedExternal)]
    [InlineData(null)]
    public void A_notice_waits_for_an_open_approval_whatever_its_principal(PrincipalClassification? principal)
    {
        // Arrange
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(OpenApproval(), durable: true, recovered: true);

        // Act
        var plan = workflow.PlanNewInputAbandonment(SessionState.Empty, Owner, principal, isChildNotice: true, nowMs: 100);

        // Assert
        Assert.IsType<NewInputPlan.WaitForApproval>(plan);
        Assert.True(workflow.HasPendingApprovals());
    }

    /// <summary>
    /// A notice has no special rights over an answered approval: the answered batch has no open prompt, so any input
    /// closes it before its own turn.
    /// </summary>
    [Fact]
    public void A_notice_does_not_wait_for_an_approval_that_has_an_answer()
    {
        // Arrange
        var workflow = new SessionToolWorkflow();
        var request = OpenApproval();
        workflow.ApplyApprovalRequested(request, durable: true, recovered: true);
        var accepted = Assert.IsAssignableFrom<ApprovalResponseResult.DurableAccepted>(workflow.PrepareRecoveredResponse(
            new ToolInteractionResponse
            {
                SessionId = Owner,
                CallId = new ToolCallId(request.CallId),
                SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                SenderId = new SenderId("alice")
            }, SessionState.Empty));
        workflow.ApplyResolvedResponse(workflow.CreateResolution(accepted, Owner, nowMs: 50));

        // Act
        var plan = workflow.PlanNewInputAbandonment(SessionState.Empty, Owner, PrincipalClassification.Operator,
            isChildNotice: true, nowMs: 100);

        // Assert
        Assert.IsType<NewInputPlan.AbandonThenActivate>(plan);
    }

    private static (SessionState State, BackgroundChildRun Run) EndedRun(bool success = true)
    {
        var run = Run();
        var state = SessionState.Empty.Apply(new ChildRunAccepted { SessionId = Owner, Run = run, RecordedAtMs = 1 });
        var delta = new WorkingContextDelta { ProjectDirectory = "/repo/child" };
        var result = new SubAgentProtocol.SubAgentResult
        {
            AgentName = run.AgentName,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            Output = "Done.",
            Completion = success
                ? new ChildRunCompletion.Completed(delta)
                : new ChildRunCompletion.Failed(SubAgentOutcomeReason.ToolExecutionFailed)
        };
        state = state.Apply(new ChildRunEvent.TerminalRecorded(new ChildRunTerminal(result, Lost: false, EvidenceWarning: null))
        {
            SessionId = Owner,
            RunId = run.RunId,
            RecordedAtMs = 2
        });
        return (state, run);
    }

    private static InputAdmitted Notice(BackgroundChildRun run, WorkingContextDelta? delta, string inputId) => new()
    {
        SessionId = Owner,
        InputId = new InputId(inputId),
        UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = $"Child run ended. run={run.RunId.Value} result_file=/x/result.md" },
        TurnContext = run.OriginalContext with { TurnId = $"turn-{inputId}" },
        ChildNotice = new ChildNotice(run.RunId, delta),
        AdmittedAtMs = 3
    };

    private static BackgroundChildRun Run() => new()
    {
        RunId = new SubAgentRunId("notice-run"),
        ScopeId = new SubAgentScopeId("notice-scope"),
        StartKey = new ChildRunStartKey.Slash(Owner, "turn-1", new InputId("slash-input")),
        AgentName = new AgentName("worker"),
        SourceOperation = "slash",
        ArgumentsDigest = "digest-1",
        OriginalContext = new TurnContextRecord
        {
            SessionId = Owner,
            TurnId = "turn-1",
            Boundary = TrustBoundary.Personal,
            RequesterSenderId = new SenderId("alice"),
            RequesterPrincipal = PrincipalClassification.Operator
        },
        InitialWorkingSnapshot = new WorkingContextSnapshot
        {
            WorkingContext = new WorkingContext { ProjectDirectory = "/repo/before" },
            Git = new GitWorkingContextInspection.Skipped()
        },
        AcceptedAtMs = 1,
        Lifecycle = new ChildRunLifecycle.Accepted(null)
    };

    private static ToolApprovalRequested OpenApproval() => new()
    {
        SessionId = Owner,
        CallId = "open-call",
        AuthorizationAttemptId = "auth-1",
        ToolName = "shell_execute",
        Patterns = ["git status"],
        CandidateVerbs = ["git"],
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = "slack",
        RequesterSenderId = new SenderId("alice"),
        RequesterPrincipal = PrincipalClassification.Operator,
        SupportsInteractiveApproval = true,
        Cwd = "/repo",
        OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
        TurnContext = new TurnContextRecord
        {
            SessionId = Owner,
            TurnId = "turn-1",
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ChannelType = "slack",
            RequesterSenderId = new SenderId("alice"),
            RequesterPrincipal = PrincipalClassification.Operator,
            SupportsInteractiveApproval = true
        },
        RequestedAtMs = 10
    };
}
