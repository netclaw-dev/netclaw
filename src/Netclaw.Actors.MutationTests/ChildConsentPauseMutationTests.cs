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
        parentOnly.ApplyApprovalRequested(ParentRequest("call-parent"), recovered: false);
        var answeredChild = new SessionToolWorkflow();
        answeredChild.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), recovered: false);
        answeredChild.ApplyResolvedResponse(Resolved("run-1/subagent-approval/1"));
        var openChild = new SessionToolWorkflow();
        openChild.ApplyApprovalRequested(ParentRequest("call-parent"), recovered: false);
        openChild.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), recovered: false);

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
    /// A person's input ends a running automation turn at a safe boundary (<c>PersonInputEndsAutomationTurnSpecs</c>).
    /// There are two safe boundaries: a tool-batch end, and the end of a compaction that the tool loop started (#2633).
    /// The flow tells the actor to start no model call: no input joins, and the next action is EndTurn. Each waiting
    /// input then starts a turn with its own authority, in admission order. An automation input does not join a turn
    /// that ends, so no input is consumed without a model call. Other input keeps the join rule. A child result notice
    /// carries the requester of its owner, but it is automated input by its kind, so it never ends a turn. An Operator
    /// or a TrustedInternal person ends the turn. An UntrustedExternal person waits, as before. The pause never holds a
    /// person, so a person also ends the turn while the pause applies. A person's slash command also ends it, and the
    /// command then drains alone (#2591). A replay is the running turn's own message, so it joins that turn.
    /// <para>
    /// At the compaction end, a restart request wins over the person. An overflow compaction resumes no tool loop: its
    /// replay continues the turn and the person waits, because the replay has no recorded authority of its own.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ReminderThenPerson", Batch, "reminder", "alice", false, "ends")]
    [InlineData("NoticeThenPerson", Batch, "notice", "alice", false, "ends")]
    [InlineData("PausedReminderThenPerson", Batch, "reminder", "alice", true, "ends")]
    [InlineData("ReminderThenReminderAndPerson", Batch, "reminder", "reminder alice", false, "ends")]
    [InlineData("ReminderThenStranger", Batch, "reminder", "stranger", false, "waits")]
    [InlineData("ReminderThenInsider", Batch, "reminder", "insider", false, "ends")]
    [InlineData("ReminderThenSlashCommand", Batch, "reminder", "/review", false, "ends")]
    [InlineData("PersonThenOtherPerson", Batch, "alice", "bob", false, "waits")]
    [InlineData("ReminderThenChildResult", Batch, "reminder", "child-result", false, "waits")]
    [InlineData("ReminderThenReminder", Batch, "reminder", "reminder", false, "joins")]
    [InlineData("ReminderThenReplay", Batch, "reminder", "replay", false, "joins")]
    [InlineData("PersonThenSamePerson", Batch, "alice", "alice", false, "joins")]
    [InlineData("CompactionReminderThenPerson", ToolLoopCompaction, "reminder", "alice", false, "ends")]
    [InlineData("CompactionNoticeThenPerson", ToolLoopCompaction, "notice", "alice", false, "ends")]
    [InlineData("CompactionReminderThenInsider", ToolLoopCompaction, "reminder", "insider", false, "ends")]
    [InlineData("CompactionReminderThenStranger", ToolLoopCompaction, "reminder", "stranger", false, "waits")]
    [InlineData("CompactionPersonThenOtherPerson", ToolLoopCompaction, "alice", "bob", false, "waits")]
    [InlineData("CompactionReminderThenChildResult", ToolLoopCompaction, "reminder", "child-result", false, "waits")]
    [InlineData("CompactionReminderThenReminder", ToolLoopCompaction, "reminder", "reminder", false, "joins")]
    [InlineData("CompactionRestartWins", RestartAtCompaction, "reminder", "alice", false, "restart")]
    [InlineData("CompactionReplayKeepsTurn", OverflowCompaction, "reminder", "replay alice", false, "replay")]
    public void A_person_input_ends_an_automation_turn_at_a_safe_boundary(
        string row, string boundary, string owner, string waiting, bool paused, string effect)
    {
        // Arrange
        var flow = new SessionInputFlow();
        flow.RestoreTurnAuthority(owner switch
        {
            "reminder" => ContextOf(Reminder("owner")),
            "notice" => NoticeContext(),
            _ => Context(owner)
        }, null);
        var contents = waiting.Split(' ');
        foreach (var kind in contents)
        {
            if (kind == "replay")
            {
                flow.BufferReplay(new SendUserMessage { SessionId = Session, Content = kind });
                continue;
            }

            flow.RestoreWaitingInputs([kind switch
            {
                "reminder" => Reminder(kind),
                "child-result" => ChildResult(kind),
                "stranger" => Input(kind, (Context(kind) with { RequesterPrincipal = PrincipalClassification.UntrustedExternal }).ToRecord()),
                "insider" => Input(kind, (Context(kind) with { RequesterPrincipal = PrincipalClassification.TrustedInternal }).ToRecord()),
                _ when kind.StartsWith('/') => Person("alice", kind),
                _ => Person(kind, kind)
            }], Session);
        }

        // Act
        var (batch, next) = Advance(flow, boundary, paused);

        // Assert
        Assert.True(next == ExpectedNext(boundary, effect), $"{row}: the next step is {next}.");
        if (effect is "joins" or "replay")
        {
            string[] joined = effect == "replay" ? ["replay"] : contents;
            Assert.Equal(joined, batch!.Messages.Select(static message => message.Content));
            if (effect == "joins")
                return;
        }
        else
        {
            Assert.True(batch is null, $"{row}: an input joined the running turn.");
            if (effect is "waits" or "restart")
                return;
        }

        // The turn ended, or the replay ran. Each other waiting input then starts its own turn, in admission order.
        Assert.False(flow.ResumesToolLoopAfterCompaction, $"{row}: the tool-loop resume is still set.");
        var served = new List<string>();
        while (flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: paused) is { Batch: { } after })
        {
            Assert.True(after.AdoptsAuthority);
            served.AddRange(after.Messages.Select(static message => message.Content));
        }

        Assert.Equal(contents.Where(static content => content != "replay"), served);
    }

    private const string Batch = "batch";
    private const string ToolLoopCompaction = "tool-loop-compaction";
    private const string RestartAtCompaction = "restart-at-compaction";
    private const string OverflowCompaction = "overflow-compaction";

    // The flow decision at the boundary, as a batch and a next step. A tool-loop compaction resumes the tool loop of
    // the running turn. An overflow compaction resumes no tool loop; its replay waits at the buffer head.
    private static (BufferedInputBatch? Batch, string Next) Advance(SessionInputFlow flow, string boundary, bool paused)
    {
        if (boundary == Batch)
        {
            var advance = flow.AdvanceAfterToolBatch(Completion(), compactionDue: false, childConsentPaused: paused);
            return (advance.Batch, advance.Next.ToString());
        }

        if (boundary != OverflowCompaction)
            flow.ResumeToolLoopAfterCompaction();
        var continuation = flow.AdvanceAfterCompaction(restartDrainRequested: boundary == RestartAtCompaction, paused);
        return (continuation.Batch, $"{continuation.Action}/resume={continuation.ResumeToolLoop}");
    }

    private static string ExpectedNext(string boundary, string effect) => (boundary, effect) switch
    {
        (Batch, "ends") => nameof(ToolBatchNextAction.EndTurn),
        (Batch, _) => nameof(ToolBatchNextAction.ContinueModel),
        (_, "ends") => $"{InputProgressAction.EndTurn}/resume=False",
        (_, "restart") => $"{InputProgressAction.Restart}/resume=False",
        (_, "replay") => $"{InputProgressAction.FollowUp}/resume=False",
        _ => $"{InputProgressAction.FollowUp}/resume=True"
    };

    private static SessionToolWorkflow Paused()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(ChildRequest("run-1", "run-1/subagent-approval/1"), recovered: false);
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
