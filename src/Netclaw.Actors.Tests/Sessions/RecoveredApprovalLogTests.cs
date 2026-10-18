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
/// lost. It logs one line for each approval that stays open, and it closes child approvals durably.
/// The journal holds four approval requests: an answered parent call, a parent call that is still open, a call of a
/// child run that the parent cancelled, and a call of a child run that was live at the restart (lost).
/// The journal has no <see cref="ChildApprovalsClosed"/> event for them. That is the shape of an old journal.
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
        Assert.True(nack.Reason is ApprovalNackReasons.PromptExpired or ApprovalNackReasons.AlreadyAnswered,
            $"late answer {callId}: nack={nack.Reason}");
        Assert.Equal(0, ToolExecutor.SuccessfulExecutions);
    }

    [Fact]
    public async Task Recovery_persists_a_close_event_for_a_stranded_child_approval_and_the_session_snapshots()
    {
        // A live child run with an open approval, and a journal with no terminal event for it.
        var sessionId = new SessionId("test-channel/stranded-child-approval");
        var (request, context) = await ParentRequestAsync(sessionId);
        var run = ChildRun("run-stranded", context);
        await SeedJournalAsync(sessionId,
            new ChildRunAccepted { SessionId = sessionId, Run = run, RecordedAtMs = 1 },
            ChildApproval(request, run.RunId));

        await JoinNewSubscriberAsync(sessionId);

        await AwaitAssertAsync(async () =>
        {
            var events = await ReadJournalAsync(sessionId);
            var closed = Assert.Single(events.Select(entry => entry.Payload).OfType<ChildApprovalsClosed>());
            Assert.Equal(run.RunId, closed.RunId);
            Assert.Equal(sessionId, closed.SessionId);
            Assert.Equal(ChildApprovalCloseReason.OwnerLost, closed.Reason);
            var closeSequence = events.Single(entry => entry.Payload is ChildApprovalsClosed).SequenceNr;

            // The closed approval no longer blocks a snapshot, so a snapshot covers the close event.
            Assert.True(await LatestSnapshotSequenceNrAsync(sessionId) >= closeSequence,
                "A snapshot must cover the close event.");
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_old_journal_without_close_events_recovers_and_closes_each_child_run_once()
    {
        var sessionId = new SessionId("test-channel/old-journal-close");
        await SeedFourCallsAsync(sessionId);

        await JoinNewSubscriberAsync(sessionId);

        await AwaitAssertAsync(async () =>
        {
            var closed = (await ReadJournalAsync(sessionId)).Select(entry => entry.Payload)
                .OfType<ChildApprovalsClosed>().ToArray();
            Assert.Equal(["run-cancelled", "run-lost"], closed.Select(item => item.RunId.Value).Order().ToArray());
            Assert.All(closed, item => Assert.Equal(ChildApprovalCloseReason.OwnerLost, item.Reason));
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replay_of_a_close_event_logs_nothing_and_writes_no_second_close_event()
    {
        var sessionId = new SessionId("test-channel/close-event-replay");
        var (request, context) = await ParentRequestAsync(sessionId);
        var run = ChildRun("run-closed-before", context);
        await SeedJournalAsync(sessionId,
            new ChildRunAccepted { SessionId = sessionId, Run = run, RecordedAtMs = 1 },
            ChildApproval(request, run.RunId),
            new ChildApprovalsClosed
            {
                SessionId = sessionId,
                RunId = run.RunId,
                Reason = ChildApprovalCloseReason.OwnerLost,
                ClosedAtMs = 3
            });

        await EventFilter.Info(contains: "Tool approval requested").ExpectAsync(0, async () =>
        {
            await JoinNewSubscriberAsync(sessionId);
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Recovery records the run as lost. When that terminal event is in the journal, recovery has finished
        // its work for the run, and the session must not have written a second close event.
        await AwaitAssertAsync(async () =>
        {
            var events = (await ReadJournalAsync(sessionId)).Select(entry => entry.Payload).ToArray();
            Assert.Contains(events.OfType<ChildRunEvent.TerminalRecorded>(), item => item.RunId == run.RunId);
            Assert.Single(events.OfType<ChildApprovalsClosed>());
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task SeedFourCallsAsync(SessionId sessionId)
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
            new ChildRunEvent.TerminalRecorded(new ChildRunTerminal(new SubAgentProtocol.SubAgentResult
            {
                AgentName = cancelledRun.AgentName,
                RunId = cancelledRun.RunId,
                ScopeId = cancelledRun.ScopeId,
                Output = "Cancelled by parent.",
                Completion = new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent)
            }, Lost: false, EvidenceWarning: null))
            {
                SessionId = sessionId,
                RunId = cancelledRun.RunId,
                RecordedAtMs = 5
            },
            new ChildRunAccepted { SessionId = sessionId, Run = lostRun, RecordedAtMs = 6 },
            ChildApproval(pending, lostRun.RunId),
            pending);
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
