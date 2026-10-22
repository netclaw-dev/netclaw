// -----------------------------------------------------------------------
// <copyright file="RecoveredApprovalLogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Persistence;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Issue 2525: recovery must not log "Tool approval requested" again for an approval that is answered, cancelled, or
/// lost. It logs one line for each approval that stays open. A cancel record or a terminal record closes the child
/// approvals of its run, in live code and in journal replay. A child owner never survives a restart, so recovery closes
/// the child approvals that remain before a stashed command runs. No new journal event is involved.
/// The journal of the main scenario holds four approval requests: an answered parent call, a parent call that is
/// still open, a call of a child run that the parent cancelled, and a call of a child run that was live at the restart.
/// </summary>
public sealed class RecoveredApprovalLogTests : ApprovalWaitTestBase
{
    private const string AnsweredCall = "call-answered";
    private const string CancelledChildCall = "run-cancelled/subagent-approval/1";
    private const string LostChildCall = "run-lost/subagent-approval/1";

    public RecoveredApprovalLogTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Recovery_logs_only_the_open_call_once()
    {
        var sessionId = new SessionId("test-channel/approval-log-count");
        await SeedFourCallsAsync(sessionId);

        // Only the open parent call logs. Replay of the other three calls writes nothing.
        await EventFilter.Info(contains: "Tool approval requested").ExpectAsync(1, async () =>
        {
            await JoinNewSubscriberAsync(sessionId);
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_line_for_the_open_call_says_that_recovery_found_it()
    {
        var sessionId = new SessionId("test-channel/approval-log-recovered");
        await SeedFourCallsAsync(sessionId);

        await EventFilter.Info(contains: $"callId={CallId} toolName=shell_execute recovered=True").ExpectAsync(1, async () =>
        {
            await JoinNewSubscriberAsync(sessionId);
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_live_request_says_that_it_is_not_recovered()
    {
        var sessionId = new SessionId("test-channel/approval-log-live");

        await EventFilter.Info(contains: $"callId={CallId} toolName=shell_execute recovered=False").ExpectAsync(1, async () =>
        {
            await ParkApprovalAsync(sessionId);
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_recovered_journal_with_only_an_answered_call_logs_nothing()
    {
        var sessionId = new SessionId("test-channel/approval-log-none");
        var (request, _) = await ParentRequestAsync(sessionId);
        var answered = request with { CallId = AnsweredCall, AuthorizationAttemptId = "auth-answered" };
        await SeedJournalAsync(sessionId, answered, Resolved(answered));

        await EventFilter.Info(contains: "Tool approval requested").ExpectAsync(0, async () =>
        {
            await JoinNewSubscriberAsync(sessionId);
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // Regression guard (passes before the fix): an answer that reaches the recovered session for an answered,
    // cancelled, or lost call runs no tool. The answer goes out with the join, so it can arrive while the session
    // recovers. The fix must keep this and must close the stale child entry before the stashed answer runs.
    [Theory]
    [InlineData(AnsweredCall)]
    [InlineData(CancelledChildCall)]
    [InlineData(LostChildCall)]
    public async Task A_late_answer_to_a_closed_call_runs_no_tool(string callId)
    {
        var sessionId = new SessionId("test-channel/late-answer-" + callId.Replace('/', '-'));
        await SeedFourCallsAsync(sessionId);

        var subscriber = CreateTestProbe("late-answer-subscriber");
        SessionManager.Tell(new JoinSession(subscriber) { SessionId = sessionId, Filter = OutputFilter.Full });
        var reply = await SessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new ToolCallId(callId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(Requester)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var nack = Assert.IsType<CommandNack>(reply);
        // The reason of a child call is the withdrawal reason (PR 2, design D6). The specs below pin each text.
        if (callId == AnsweredCall)
            Assert.True(nack.Reason is ApprovalNackReasons.PromptExpired or ApprovalNackReasons.AlreadyAnswered,
                $"late answer {callId}: nack={nack.Reason}");
        Assert.Equal(0, ToolExecutor.SuccessfulExecutions);
    }

    // The rule: replay of a cancel record, a terminal record, or a lost record closes the child approvals of its run,
    // and recovery closes the child approvals of a run that was live at the restart. Replay logs none of them.
    [Theory]
    [InlineData("cancel-record")]
    [InlineData("terminal-record")]
    [InlineData("lost-record")]
    [InlineData("owner-lost")]
    public async Task Replay_of_a_closed_child_approval_logs_nothing(string scenario)
    {
        var sessionId = new SessionId("test-channel/closed-child-" + scenario);
        var (request, context) = await ParentRequestAsync(sessionId);
        var run = ChildRun("run-" + scenario, context);
        var journal = new List<ISessionEvent>
        {
            new ChildRunAccepted { SessionId = sessionId, Run = run, RecordedAtMs = 1 },
            ChildApproval(request, run.RunId)
        };
        switch (scenario)
        {
            case "cancel-record":
                journal.Add(new ChildRunEvent.CancellationRequested
                { SessionId = sessionId, RunId = run.RunId, RecordedAtMs = 3 });
                break;
            case "terminal-record":
                journal.Add(Terminal(sessionId, run, lost: false,
                    new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent)));
                break;
            case "lost-record":
                journal.Add(Terminal(sessionId, run, lost: true,
                    new ChildRunCompletion.Failed(SubAgentOutcomeReason.ActorStopped)));
                break;
        }

        await SeedJournalAsync(sessionId, journal.ToArray());

        await EventFilter.Info(contains: "Tool approval requested").ExpectAsync(0, async () =>
        {
            await JoinNewSubscriberAsync(sessionId);
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // The stashed answer runs right after RecoveryCompleted, before the RecoverChildRuns self message. The child entry
    // must already be closed then. If the entry were still open, the answer would pass validation, and only the
    // per-incarnation approval channel would stop it ("no_live_wait") with the expired text.
    // PR 2 of child-approval-events-and-parent-pause (design D6): the closed child call gets the true withdrawal text.
    // The session reads the reason from the run lifecycle and the run ID prefix of the call ID:
    // - a run that the parent cancelled before the restart: "cancelled by parent";
    // - a run that was live at the restart: "daemon restarted", also before RecoverChildRuns records it as lost;
    // - a run that ended with its prompt open: "child ended". Only a journal can show this case, because a live child
    //   waits for its prompt and cannot end first.
    // No text names the run or the agent, and the nack is not the "expired" nack.
    // Planted bug: the click gets "That approval prompt has expired" (today), or a reason that does not match the run.
    [Theory]
    [InlineData("cancelled")]
    [InlineData("lost-at-restart")]
    [InlineData("ended")]
    public async Task A_late_answer_to_a_closed_child_call_gets_the_withdrawal_reason_of_its_run(string scenario)
    {
        var sessionId = new SessionId("test-channel/late-child-answer-" + scenario);
        if (scenario != "ended")
            await SeedFourCallsAsync(sessionId);
        var (callId, reason) = scenario switch
        {
            "cancelled" => (CancelledChildCall, WithdrawReason.CancelledByParent),
            "lost-at-restart" => (LostChildCall, WithdrawReason.DaemonRestarted),
            _ => (await SeedEndedChildCallAsync(sessionId, answered: false), WithdrawReason.ChildEnded)
        };
        var noticeTurn = HoldTheNoticeTurnOfTheEndedRun();
        try
        {
            var subscriber = CreateTestProbe("late-child-answer-subscriber");
            SessionManager.Tell(new JoinSession(subscriber) { SessionId = sessionId, Filter = OutputFilter.Full });
            var reply = await SessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
            {
                SessionId = sessionId,
                CallId = new ToolCallId(callId),
                SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                SenderId = new SenderId(Requester)
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var notice = await subscriber.FishForMessageAsync<TextOutput>(
                output => ChildWithdrawalTexts.IsApprovalReply(output.Text), TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ChildWithdrawalTexts.For(reason), notice.Text);
            Assert.NotEqual(ApprovalNackReasons.PromptExpired, Assert.IsType<CommandNack>(reply).Reason);
            Assert.DoesNotContain(callId.Split('/')[0], notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("worker", notice.Text, StringComparison.Ordinal);
            Assert.Equal(0, ToolExecutor.SuccessfulExecutions);
        }
        finally
        {
            noticeTurn.TrySetResult();
        }
    }

    // The withdrawal text says "Nothing ran". A prompt that has a recorded answer did not close with no answer, so a
    // second click on it after its run ended gets the "already has an answer" reply and never a withdrawal text.
    // Planted bug: the new withdrawal case reads only the run lifecycle and tells the operator that nothing ran for a
    // call that the operator approved and that ran; or the click gets the expired text (today).
    [Fact]
    public async Task A_late_answer_to_an_answered_call_of_an_ended_child_says_that_the_prompt_has_an_answer()
    {
        var sessionId = new SessionId("test-channel/late-child-answer-answered");
        var callId = await SeedEndedChildCallAsync(sessionId, answered: true);
        var noticeTurn = HoldTheNoticeTurnOfTheEndedRun();
        try
        {
            var subscriber = await JoinNewSubscriberAsync(sessionId);
            var reply = await SessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
            {
                SessionId = sessionId,
                CallId = new ToolCallId(callId),
                SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                SenderId = new SenderId(Requester)
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var notice = await subscriber.FishForMessageAsync<TextOutput>(
                output => ChildWithdrawalTexts.IsApprovalReply(output.Text), TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Approval response ignored: that approval prompt already has an answer.", notice.Text);
            Assert.Equal(ApprovalNackReasons.AlreadyAnswered, Assert.IsType<CommandNack>(reply).Reason);
            Assert.Equal(0, ToolExecutor.SuccessfulExecutions);
        }
        finally
        {
            noticeTurn.TrySetResult();
        }
    }

    // The journal of an ended run holds no consumed result notice. The recovered session admits the notice and runs a
    // notice turn. With SnapshotInterval = 1, the end of that turn saves a snapshot, and the snapshot forgets the closed
    // child calls (LlmSessionActor, SaveSnapshotSuccess). An answer that the session handles after that snapshot gets the
    // "expired" reply by design. These specs check the reply while the session still knows the closed call, so they hold
    // the model call of the notice turn until the session has replied. Only the notice turn calls the model.
    // Without the hold, a loaded test run sometimes handled the answer after the snapshot.
    private TaskCompletionSource HoldTheNoticeTurnOfTheEndedRun()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeChat.NextResponseGate = hold;
        return hold;
    }

    // Regression guard (passes before the fix): an old journal with answered, cancelled, and lost calls and no open
    // parent call recovers, and the closed child entries do not block the snapshot.
    [Fact]
    public async Task An_old_journal_recovers_and_the_session_snapshots_again()
    {
        var sessionId = new SessionId("test-channel/old-journal-snapshot");
        await SeedFourCallsAsync(sessionId, includeOpenParentCall: false);

        await JoinNewSubscriberAsync(sessionId);

        await AwaitAssertAsync(async () =>
        {
            var last = (await ReadJournalAsync(sessionId)).Max(entry => entry.SequenceNr);
            Assert.True(await LatestSnapshotSequenceNrAsync(sessionId) >= last,
                "A snapshot must cover the whole journal after recovery.");
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task SeedFourCallsAsync(SessionId sessionId, bool includeOpenParentCall = true)
    {
        var (pending, context) = await ParentRequestAsync(sessionId);
        var answered = pending with { CallId = AnsweredCall, AuthorizationAttemptId = "auth-answered" };
        var cancelledRun = ChildRun("run-cancelled", context);
        var lostRun = ChildRun("run-lost", context);
        await SeedJournalAsync(sessionId,
            answered,
            Resolved(answered),
            new ChildRunAccepted { SessionId = sessionId, Run = cancelledRun, RecordedAtMs = 3 },
            ChildApproval(pending, cancelledRun.RunId),
            Terminal(sessionId, cancelledRun, lost: false,
                new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent)),
            new ChildRunAccepted { SessionId = sessionId, Run = lostRun, RecordedAtMs = 6 },
            ChildApproval(pending, lostRun.RunId));
        if (includeOpenParentCall)
            await SeedJournalAsync(sessionId, pending);
    }

    // A child run that ended normally while its prompt was open (answered: false), or after its prompt had an answer
    // (answered: true). Returns the call ID of the prompt.
    private async Task<string> SeedEndedChildCallAsync(SessionId sessionId, bool answered)
    {
        var (pending, context) = await ParentRequestAsync(sessionId);
        var endedRun = ChildRun("run-ended", context);
        var request = ChildApproval(pending, endedRun.RunId);
        var events = new List<ISessionEvent>
        {
            new ChildRunAccepted { SessionId = sessionId, Run = endedRun, RecordedAtMs = 3 },
            request
        };
        if (answered)
            events.Add(Resolved(request));
        events.Add(Terminal(sessionId, endedRun, lost: false, new ChildRunCompletion.Completed(new WorkingContextDelta())));
        await SeedJournalAsync(sessionId, events.ToArray());
        return request.CallId;
    }

    // Copies a real parent approval request, so the seeded events carry a valid turn authority.
    private async Task<(ToolApprovalRequested Request, TurnContextRecord Context)> ParentRequestAsync(SessionId sessionId)
    {
        var source = new SessionId("test-channel/approval-log-source-" + Guid.NewGuid().ToString("N"));
        await ParkApprovalAsync(source);
        var request = (await ReadJournalAsync(source)).Select(entry => entry.Payload)
            .OfType<ToolApprovalRequested>().Single();
        await StopSessionAsync(source);

        var context = request.TurnContext! with { SessionId = sessionId };
        return (request with { SessionId = sessionId, TurnContext = context }, context);
    }

    private static ChildRunEvent.TerminalRecorded Terminal(
        SessionId sessionId, BackgroundChildRun run, bool lost, ChildRunCompletion completion)
        => new(new ChildRunTerminal(new SubAgentProtocol.SubAgentResult
        {
            AgentName = run.AgentName,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            Output = "Closed.",
            Completion = completion
        }, lost, EvidenceWarning: null))
        {
            SessionId = sessionId,
            RunId = run.RunId,
            RecordedAtMs = 5
        };

    private static ToolApprovalResolved Resolved(ToolApprovalRequested request)
        => new()
        {
            SessionId = request.SessionId,
            CallId = request.CallId,
            AuthorizationAttemptId = request.AuthorizationAttemptId,
            Decision = ConsentAnswerCodec.ToJournalText(ConsentAnswer.Once.Instance),
            ResolvedAtMs = 2
        };

    private static ToolApprovalRequested ChildApproval(ToolApprovalRequested parent, SubAgentRunId runId)
        => parent with
        {
            CallId = $"{runId.Value}/subagent-approval/1",
            AuthorizationAttemptId = $"auth-{runId.Value}",
            SourceChildRunId = runId,
            OriginalChildCallId = new ToolCallId("child-call-1")
        };

    private static BackgroundChildRun ChildRun(string runId, TurnContextRecord context)
        => new()
        {
            RunId = new SubAgentRunId(runId),
            ScopeId = new SubAgentScopeId($"scope-{runId}"),
            StartKey = new ChildRunStartKey.Slash(context.SessionId, context.TurnId, new InputId($"input-{runId}")),
            AgentName = new AgentName("worker"),
            SourceOperation = "slash",
            ArgumentsDigest = $"digest-{runId}",
            OriginalContext = context,
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = new WorkingContext { ProjectDirectory = "/repo" },
                Git = new GitWorkingContextInspection.Skipped()
            },
            AcceptedAtMs = 1,
            SourceReminderId = null,
            Lifecycle = new ChildRunLifecycle.Accepted(null)
        };

    private async Task SeedJournalAsync(SessionId sessionId, params ISessionEvent[] events)
    {
        var seeder = Sys.ActorOf(Props.Create(() => new JournalSeeder($"session-{sessionId.Value}")));
        foreach (var evt in events)
            await seeder.Ask<Akka.Done>(evt, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class JournalSeeder : ReceivePersistentActor
    {
        public JournalSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<object>(_ => { });
            Command<ISessionEvent>(evt =>
            {
                var replyTo = Sender;
                Persist(evt, _ => replyTo.Tell(Akka.Done.Instance));
            });
        }

        public override string PersistenceId { get; }
    }
}
