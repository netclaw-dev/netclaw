// -----------------------------------------------------------------------
// <copyright file="ChildConsentPauseMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// The child consent pause (child-approval-events-and-parent-pause, design D5). An open child consent request pauses
/// the session: the model cannot start a child, and automated input and child result notices wait. A person input
/// still runs, and an approval notice is never held and runs alone. A mutant that weakens the pause lets automation
/// run while the operator must answer. A mutant that widens it blocks a person or the notice that explains the pause.
/// <c>scripts/run-child-consent-mutations.sh</c> runs these tests. No test uses an actor, a timer, or a delay.
/// </summary>
public sealed class ChildConsentPauseMutationTests
{
    private static readonly SessionId Session = new("test/child-consent-pause");

    [Fact]
    public void Only_an_open_child_request_pauses_the_session()
    {
        var empty = new SessionToolWorkflow();
        var parentOnly = new SessionToolWorkflow();
        parentOnly.ApplyApprovalRequested(ParentRequest("call-parent"), durable: true, recovered: false);
        var answeredChild = new SessionToolWorkflow();
        answeredChild.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);
        answeredChild.ApplyResolvedResponse(Resolved("run-1/subagent-approval/1"));
        var openChild = new SessionToolWorkflow();
        openChild.ApplyApprovalRequested(ParentRequest("call-parent"), durable: true, recovered: false);
        openChild.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);

        Assert.False(empty.IsChildConsentPaused);
        Assert.False(parentOnly.IsChildConsentPaused);
        Assert.False(answeredChild.IsChildConsentPaused);
        Assert.True(openChild.IsChildConsentPaused);
    }

    [Fact]
    public void A_paused_session_refuses_a_model_start_and_accepts_a_slash_start()
    {
        var paused = Paused();
        var clear = new SessionToolWorkflow();
        var tool = new ChildRunStartKey.Tool(Session, "turn-1", new ToolCallId("spawn-1"));
        var slash = new ChildRunStartKey.Slash(Session, "turn-1", new InputId("input-1"));

        Assert.True(paused.RefusesChildStart(tool));
        Assert.False(paused.RefusesChildStart(slash));
        Assert.False(clear.RefusesChildStart(tool));
    }

    [Fact]
    public void A_paused_session_holds_automated_input_and_a_child_result_and_runs_a_person()
    {
        var paused = Paused();
        var clear = new SessionToolWorkflow();

        Assert.IsType<NewInputPlan.WaitForApproval>(Plan(paused, PrincipalClassification.VerifiedAutomation, isChildNotice: false));
        Assert.IsType<NewInputPlan.WaitForApproval>(Plan(paused, PrincipalClassification.SystemProcess, isChildNotice: false));
        Assert.IsType<NewInputPlan.WaitForApproval>(Plan(paused, PrincipalClassification.Operator, isChildNotice: true));
        Assert.IsType<NewInputPlan.Activate>(Plan(paused, PrincipalClassification.Operator, isChildNotice: false));
        Assert.IsType<NewInputPlan.Activate>(Plan(clear, PrincipalClassification.VerifiedAutomation, isChildNotice: false));
    }

    [Fact]
    public void A_new_turn_skips_held_input_and_the_held_input_runs_after_the_pause()
    {
        // Arrange: a reminder fire and a child result notice wait in front of a person input.
        var flow = new SessionInputFlow();
        flow.RestoreWaitingInputs([Reminder("r1"), ChildResult("n1"), Person("alice", "p1")], Session);

        // Act
        var whilePaused = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: true);
        var stillPaused = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: true);
        var first = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);
        var second = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);
        var end = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);

        // Assert: the person runs first. The held input keeps its order and runs once, after the pause.
        Assert.Equal(["p1"], Contents(whilePaused));
        Assert.Equal(InputProgressAction.Ready, stillPaused.Action);
        Assert.Equal(["r1"], Contents(first));
        Assert.Equal(["n1"], Contents(second));
        Assert.Equal(InputProgressAction.Ready, end.Action);
    }

    [Fact]
    public void Without_the_pause_the_buffer_keeps_its_order()
    {
        var flow = new SessionInputFlow();
        flow.RestoreWaitingInputs([Reminder("r1"), Person("alice", "p1")], Session);

        var first = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);
        var second = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);

        Assert.Equal(["r1"], Contents(first));
        Assert.Equal(["p1"], Contents(second));
    }

    [Fact]
    public void A_held_child_result_does_not_join_the_running_turn_of_its_owner()
    {
        // The child result has the recorded requester of Alice, so it joins her turn when no pause applies.
        var paused = Running(Context("alice"), ChildResult("n1"));
        var clear = Running(Context("alice"), ChildResult("n1"));

        var held = paused.AdvanceAfterToolBatch(Completion(), compactionDue: false, childConsentPaused: true);
        var joined = clear.AdvanceAfterToolBatch(Completion(), compactionDue: false, childConsentPaused: false);

        Assert.Null(held.Batch);
        Assert.Equal(["n1"], joined.Batch!.Messages.Select(static message => message.Content));
    }

    [Fact]
    public void An_approval_notice_passes_the_pause_and_starts_its_own_turn()
    {
        var flow = new SessionInputFlow();
        flow.RestoreWaitingInputs([Reminder("r1"), Notice("notice-1")], Session);

        var notice = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: true);

        Assert.Equal(["notice-1"], Contents(notice));
        Assert.True(notice.Batch!.AdoptsAuthority);
    }

    [Fact]
    public void Nothing_joins_a_notice_turn_and_a_notice_joins_no_turn()
    {
        // A webhook can name its sender child-consent. It has the same requester as a notice, but it is not a notice.
        var noticeTurn = Running(NoticeContext(), Poser("webhook-1"));
        var poserTurn = Running(PoserContext(), Notice("notice-1"));
        var twoNotices = new SessionInputFlow();
        twoNotices.RestoreWaitingInputs([Notice("notice-1"), Notice("notice-2")], Session);

        var intoNotice = noticeTurn.AdvanceAfterToolBatch(Completion(), compactionDue: false, childConsentPaused: false);
        var intoPoser = poserTurn.AdvanceAfterToolBatch(Completion(), compactionDue: false, childConsentPaused: false);
        var first = twoNotices.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);
        var second = twoNotices.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: false);

        Assert.Null(intoNotice.Batch);
        Assert.Null(intoPoser.Batch);
        Assert.Equal(["notice-1"], Contents(first));
        Assert.Equal(["notice-2"], Contents(second));
    }

    [Fact]
    public void A_replay_continues_its_turn_while_paused()
    {
        var flow = new SessionInputFlow();
        flow.RestoreTurnAuthority(Context("alice"), null);
        flow.BufferReplay(new SendUserMessage { SessionId = Session, Content = "replay" });

        var replay = flow.AdvanceAfterCompaction(restartDrainRequested: false, childConsentPaused: true);

        Assert.Equal(["replay"], replay.Batch!.Messages.Select(static message => message.Content));
    }

    /// <summary>
    /// A person's input ends a running automation turn at the tool-batch end (<c>PersonInputEndsAutomationTurnSpecs</c>).
    /// The flow tells the actor to start no model call: no input joins, and the next action is not ContinueModel. Each
    /// waiting input then starts a turn with its own authority. An automation input does not join a turn that ends, so
    /// no input is consumed without a model call. Other input keeps the join rule. A child result notice carries the
    /// requester of its owner, but it is automated input by its kind, so it never ends a turn. The pause never holds a
    /// person, so a person also ends the turn while the pause applies. An admitted non-operator person and a person's
    /// slash command also end it: ending a turn early grants no authority, and the command then drains alone (#2591).
    /// </summary>
    [Theory]
    [InlineData("reminder", "alice", false, "ends")]
    [InlineData("notice", "alice", false, "ends")]
    [InlineData("reminder", "alice", true, "ends")]
    [InlineData("reminder", "reminder alice", false, "ends")]
    [InlineData("reminder", "stranger", false, "ends")]
    [InlineData("reminder", "/review", false, "ends")]
    [InlineData("alice", "bob", false, "waits")]
    [InlineData("reminder", "child-result", false, "waits")]
    [InlineData("reminder", "reminder", false, "joins")]
    [InlineData("alice", "alice", false, "joins")]
    public void A_person_input_ends_an_automation_turn_at_the_tool_batch_end(string owner, string waiting, bool paused, string effect)
    {
        // Arrange
        var flow = new SessionInputFlow();
        flow.RestoreTurnAuthority(owner switch
        {
            "reminder" => ContextOf(Reminder("owner")),
            "notice" => NoticeContext(),
            _ => Context(owner)
        }, null);
        var inputs = waiting.Split(' ').Select(static kind => kind switch
        {
            "reminder" => Reminder(kind),
            "child-result" => ChildResult(kind),
            "stranger" => Input(kind, (Context(kind) with { RequesterPrincipal = PrincipalClassification.UntrustedExternal }).ToRecord()),
            _ when kind.StartsWith('/') => Person("alice", kind),
            _ => Person(kind, kind)
        }).ToArray();
        flow.RestoreWaitingInputs(inputs, Session);
        var contents = inputs.Select(static input => input.UserMessage.Content).ToArray();

        // Act
        var advance = flow.AdvanceAfterToolBatch(Completion(), compactionDue: false, childConsentPaused: paused);

        // Assert
        if (effect == "joins")
        {
            Assert.Equal(contents, advance.Batch!.Messages.Select(static message => message.Content));
            Assert.Equal(ToolBatchNextAction.ContinueModel, advance.Next);
            return;
        }

        Assert.Null(advance.Batch);
        if (effect == "waits")
        {
            Assert.Equal(ToolBatchNextAction.ContinueModel, advance.Next);
            return;
        }

        Assert.NotEqual(ToolBatchNextAction.ContinueModel, advance.Next);
        var served = new List<string?>();
        while (flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: paused) is { Batch: { } batch })
        {
            Assert.True(batch.AdoptsAuthority);
            served.AddRange(batch.Messages.Select(static message => message.Content));
        }

        Assert.Equal(contents.Order(), served.Order());
    }

    private static SessionToolWorkflow Paused()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), durable: true, recovered: false);
        return workflow;
    }

    private static NewInputPlan Plan(SessionToolWorkflow workflow, PrincipalClassification principal, bool isChildNotice)
        => workflow.PlanNewInputAbandonment(SessionState.Empty, Session, principal, isChildNotice, 1);

    private static SessionInputFlow Running(TurnContext owner, InputAdmitted buffered)
    {
        var flow = new SessionInputFlow();
        flow.RestoreTurnAuthority(owner, null);
        flow.RestoreWaitingInputs([buffered], Session);
        return flow;
    }

    private static BatchCompletion.Ready Completion() => new(SessionState.Empty, 1);

    private static IEnumerable<string?> Contents(AfterTurnResult result)
    {
        Assert.Equal(InputProgressAction.FollowUp, result.Action);
        return result.Batch!.Messages.Select(static message => message.Content);
    }

    private static InputAdmitted Person(string sender, string content) => Input(content, Context(sender).ToRecord());

    private static InputAdmitted Reminder(string content) => Input(content, (Context("reminder-system") with
    {
        RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("reminder")
        }
    }).ToRecord());

    // A child result notice carries the recorded requester of the person who owns the run.
    private static InputAdmitted ChildResult(string content) => Input(content, Context("alice").ToRecord()) with
    {
        ChildNotice = new ChildNotice(new SubAgentRunId($"run-{content}"), null)
    };

    private static InputAdmitted Notice(string content)
        => SessionInputFlow.PrepareConsentNotice(Session, Context("alice").ToRecord(), content, 1);

    private static InputAdmitted Poser(string content) => Input(content, PoserContext().ToRecord());

    private static TurnContext NoticeContext() => ContextOf(Notice("context"));

    private static TurnContext ContextOf(InputAdmitted input)
        => TurnContext.TryFromRecord(input.TurnContext, out var context, out _) ? context! : throw new InvalidOperationException();

    private static TurnContext PoserContext() => NoticeContext() with
    {
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("child-consent")
        }
    };

    private static InputAdmitted Input(string content, TurnContextRecord context) => new()
    {
        SessionId = Session,
        InputId = InputId.New(),
        UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = content },
        TurnContext = context with { TurnId = $"turn-{content}" },
        AdmittedAtMs = 1
    };

    private static TurnContext Context(string sender) => new()
    {
        SessionId = Session,
        TurnId = new TurnId($"turn-{sender}"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.Slack,
        RequesterSenderId = new SenderId(sender),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };

    private static ToolApprovalResolved Resolved(string callId) => new()
    {
        SessionId = Session,
        CallId = callId,
        Decision = ConsentAnswerCodec.ToJournalText(ConsentAnswer.Once.Instance),
        ResolvedAtMs = 5
    };

    private static ToolApprovalRequested ChildRequest(string runId, string callId) => ParentRequest(callId) with
    {
        SourceChildRunId = new SubAgentRunId(runId),
        OriginalChildCallId = new ToolCallId("child-call-1")
    };

    private static ToolApprovalRequested ParentRequest(string callId)
    {
        var context = Context("alice");
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
