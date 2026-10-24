// -----------------------------------------------------------------------
// <copyright file="ChildParkSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// PR 1 and PR 2 of durable-child-consent-wait (issue #2572), S1 to S4. Each row runs one child run of Alice to its end
/// and then checks the shared run invariants (<see cref="ChildParkTestBase.AssertRunInvariantsAsync"/>). Each row that
/// parks first checks the park precondition: the journal holds the Parked record. Before PR 2 the park is actor-local,
/// so each row fails there or at the invariant that the durable park changes.
/// Source: background-subagent-runs, tool-authorization TA-12, design D1 to D4 and D7 of durable-child-consent-wait,
/// and design D1 to D7 of durable-child-park-recovery.
/// </summary>
public sealed class ChildParkSpecs(ITestOutputHelper output) : ChildParkTestBase(output)
{
    private const string BobTurn = "Bob keeps a turn open.";
    private ToolAudienceProfile? _personal;

    protected override void ConfigurePersonalProfile(ToolAudienceProfile profile) => _personal = profile;

    protected override void RegisterParentTools(ToolRegistry registry)
    {
        base.RegisterParentTools(registry);
        registry.RegisterCore(new ProjectTool());
    }

    /// <summary>
    /// S1, park and resume. Rows: a call and a sibling; two calls; a new call after the resume; a deny rule after the
    /// park; a parent that moved its project directory; a sibling that sets the child directory; a sibling that changes
    /// a file. Bob's turn is open while Alice answers. Bob answers first and gets the wrong-requester nack. A second
    /// answer after the end gets "already answered".
    /// Planted bug: the resume runs the batch again, resumes on the first of two answers, keeps the consent for a new
    /// call, skips the gate, takes the authority or the directory of the current turn, or loses the directory or the
    /// file activity that a finished sibling of the parked batch made (design D2, batch effects).
    /// </summary>
    [Theory]
    [InlineData("sibling")]
    [InlineData("two")]
    [InlineData("newcall")]
    [InlineData("deny")]
    [InlineData("moved")]
    [InlineData("cwd")]
    [InlineData("files")]
    public async Task A_parked_run_resumes_once_under_the_run_authority(string row)
    {
        // Arrange
        var id = new SessionId($"park/resume-{row}");
        var journal = await JournalAsync();
        ScriptParentWithProjects();
        var recorded = Directory.CreateDirectory(Path.Combine(TestPaths.WorkspacesDirectory, "recorded")).FullName;
        var moved = Directory.CreateDirectory(Path.Combine(TestPaths.WorkspacesDirectory, "moved")).FullName;
        var changed = Path.Combine(recorded, "changed.txt");
        IReadOnlyList<AIContent>[] plan = row switch
        {
            "sibling" => [[Probe("p1"), SiblingCall("s1")]],
            "two" => [[Probe("p1"), Probe("p2")]],
            "newcall" => [[Probe("p1")], [Probe("p2")]],
            "cwd" => [[Probe("p1"), ProjectCall("c1", moved)], [Probe("p2")]],
            "files" => [[Probe("p1"), ChangeFile("f1", changed)]],
            _ => [[Probe("p1")]]
        };
        var (parked, prompts, probeRuns) = row switch
        {
            "two" => (2, 2, 2),
            "newcall" or "cwd" => (1, 2, 2),
            "deny" => (1, 1, 0),
            _ => (1, 1, 1)
        };
        PlanChild($"park-{row}", plan);
        await JoinRecorderAsync(id);
        if (row is "moved" or "cwd")
            await DeclareProjectAsync(journal, id, recorded);
        var run = await StartAsync(id, $"park-{row}");
        var first = await AwaitPromptsAsync(run.Run.RunId, parked);
        await AssertParkedAsync(journal, id, run, parked);
        if (row == "deny")
            _personal!.ApprovalPolicy!.ToolOverrides["authority_probe"] = ToolApprovalMode.Deny;
        if (row == "moved")
            await DeclareProjectAsync(journal, id, moved);
        var bobTurn = await HoldTurnAsync(journal, id, BobTurn, OperatorWithTargets(Bob, BobTarget));

        // Act
        Assert.Equal(ApprovalNackReasons.WrongRequester,
            Assert.IsType<CommandNack>(await AnswerAsync(id, first[0].CallId, Bob)).Reason);
        for (var answered = 0; answered < prompts; answered++)
        {
            var prompt = (await AwaitPromptsAsync(run.Run.RunId, answered + 1))[answered];
            AssertAck(await AnswerAsync(id, prompt.CallId, Alice));
            if (answered + 1 < parked)
            {
                await BarrierAsync(id, run);
                Assert.Empty(ApprovalTool.Contexts);
            }
        }

        bobTurn.TrySetResult();

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns);
        Assert.True(terminal.Result.Success, terminal.Result.Output);
        var resumed = Clients.A.ReceivedMessages.First(request => HasResult(request, "p1"));
        Assert.Single(Results(resumed), result => result.CallId == "p1");
        if (row == "sibling")
            Assert.Single(Results(resumed), result => result.CallId == "s1");
        if (row == "deny")
            Assert.NotEqual("confirmed", Results(resumed).Single(result => result.CallId == "p1").Result?.ToString());
        if (row == "moved")
            Assert.Equal(recorded, Assert.Single(ApprovalTool.Contexts).ProjectDirectory);
        // H3: the parked call runs in the directory of its batch. The next call runs where the finished sibling moved.
        if (row == "cwd")
            Assert.Equal([recorded, moved], ApprovalTool.Contexts.Select(static context => context.ProjectDirectory));
        if (row == "files")
            Assert.Contains(changed, terminal.Result.WorkingContext?.ConfirmedChangedFiles ?? []);
        Assert.Equal(ApprovalNackReasons.AlreadyAnswered,
            Assert.IsType<CommandNack>(await AnswerAsync(id, first[0].CallId, Alice)).Reason);
    }

    /// <summary>
    /// S2, refusals. Rows: the journal rejects the park during a parent turn; the journal rejects the park of two
    /// requests; the agent profile is deleted before the answer; the model call of the resumed child fails; the journal
    /// rejects the answer; the park is above the size cap; the journal rejects the Resumed record; a duplicate answer
    /// from the stash resumes the run before the rejection of the first answer arrives. Each run ends once, and no
    /// parent turn fails.
    /// Planted bug: a rejected park fails the parent turn, one park takes more than one atomic write, a missing profile
    /// resumes with a stale profile, a failed resume records two ends, a rejected answer leaves the run open, a park
    /// above the cap shows a prompt, a rejected Resumed record starts the child, or a late rejection ends a resumed run.
    /// </summary>
    [Theory]
    [InlineData("rejected")]
    [InlineData("atomic")]
    [InlineData("profile")]
    [InlineData("model")]
    [InlineData("answer")]
    [InlineData("toolarge")]
    [InlineData("resume-rejected")]
    [InlineData("late-rejection")]
    public async Task A_refused_park_or_resume_ends_the_run_failed_once(string row)
    {
        // Arrange
        var id = new SessionId($"park/refused-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        var twoRequests = row is "rejected" or "atomic";
        PlanChild($"park-{row}", row switch
        {
            "rejected" or "atomic" => [[Probe("p1"), Probe("p2")]],
            // The assistant text alone is above the cap (PR 2 design D4).
            "toolarge" => [[new TextContent(new string('x', ChildRunPark.MaxBytes + 1)), Probe("p1")]],
            _ => [[Probe("p1")]]
        });
        var profiles = Host.Services.GetRequiredService<SubAgentDefinitionRegistry>();
        profiles.ReplaceFileProfiles([new SubAgentProfile
        {
            Name = "child-c", Description = "child-c", SystemPrompt = "child-c", ModelRole = ModelRole.Fallback,
            ToolNames = ["authority_probe"], Visibility = SubAgentVisibility.UserFacing
        }]);
        await JoinRecorderAsync(id);
        TaskCompletionSource? aliceTurn = null;
        if (twoRequests)
            journal.RejectWritesOf(static evt => evt is ChildRunEvent.Parked);

        // Act
        var run = await StartAsync(id, $"park-{row}", row == "profile" ? "child-c" : "child-a",
            row == "rejected" ? async () => aliceTurn = await HoldTurnAsync(journal, id, "Alice keeps a turn open.", AliceSource) : null);
        if (twoRequests)
        {
            await journal.FirstEventRejected.Task.WaitAsync(Deadline, Cancellation);
            await BarrierAsync(id, run);
            aliceTurn?.TrySetResult();
            // One park writes all its requests in one atomic write, so a rejected write leaves no request of the park.
            Assert.Empty(RequestsOf(journal, run.Run.RunId));
        }
        else if (row != "toolarge")
        {
            var prompt = (await AwaitPromptsAsync(run.Run.RunId, 1))[0];
            await AssertParkedAsync(journal, id, run, 1);
            switch (row)
            {
                case "profile":
                    profiles.ReplaceFileProfiles([]);
                    break;
                case "model":
                    Clients.A.PlannedExceptions.Enqueue(new InvalidOperationException("The model is down."));
                    break;
                case "answer":
                    // An open parent turn proves that the child branch of OnPersistRejected runs before the turn failure.
                    aliceTurn = await HoldTurnAsync(journal, id, "Alice keeps a turn open.", AliceSource);
                    journal.RejectWritesOf(static evt => evt is ToolApprovalResolved);
                    break;
                case "resume-rejected":
                    journal.RejectWritesOf(static evt => evt is ChildRunEvent.Resumed);
                    break;
                case "late-rejection":
                    journal.RejectWritesOf(static evt => evt is ToolApprovalResolved);
                    journal.HoldNext(static evt => evt is ToolApprovalResolved);
                    break;
            }
            if (row == "answer")
            {
                Assert.Equal(ApprovalNackReasons.PersistFailed,
                    Assert.IsType<CommandNack>(await AnswerAsync(id, prompt.CallId, Alice)).Reason);
                aliceTurn!.TrySetResult();
            }
            else if (row == "late-rejection")
                await AnswerTwiceAsync(journal, id, prompt.CallId);
            else
                AssertAck(await AnswerAsync(id, prompt.CallId, Alice));
        }

        // Assert
        var resumes = row is "model" or "late-rejection";
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: resumes ? 1 : 0);
        Assert.Equal(row == "late-rejection" ? SubAgentRunOutcome.Completed : SubAgentRunOutcome.Failed, terminal.Result.Outcome);
        if (row == "toolarge")
        {
            Assert.Equal(SubAgentOutcomeReason.ParkTooLarge, terminal.Result.OutcomeReason);
            Assert.Empty(PromptsOf(run.Run.RunId));
        }
        if (row == "resume-rejected")
        {
            Assert.Equal(SubAgentOutcomeReason.ToolExecutionFailed, terminal.Result.OutcomeReason);
            Assert.Contains("The parked calls did not run.", terminal.Result.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// S3, cancel (design D7). Rows: a cancel while the run is parked; a cancel while the batch runs; a cancel after a
    /// restart; a cancel that waits in the stash while the journal rejects the park. The run ends cancelled with one
    /// terminal record, and the parked call never runs. A cancel during the batch leaves no request. After the restart, a
    /// late click gets the "cancelled" text.
    /// Planted bug: the cancel of a parked run waits for an actor that does not exist, a late park shows a prompt, a
    /// restart loses the park that the cancel must end, or a cancel after a rejected park leaves the run cancelling with
    /// no actor.
    /// </summary>
    [Theory]
    [InlineData("parked")]
    [InlineData("batch")]
    [InlineData("restart")]
    [InlineData("park-rejected")]
    public async Task A_cancelled_run_ends_cancelled_once(string row)
    {
        // Arrange
        var id = new SessionId($"park/cancel-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild($"park-{row}", [[Probe("p1"), SiblingCall("s1")]]);
        await JoinRecorderAsync(id);
        var siblingStarted = row == "batch" ? Sibling.HoldNext() : Task.CompletedTask;
        if (row == "park-rejected")
        {
            // The journal holds the park write, and then rejects it.
            journal.RejectWritesOf(static evt => evt is ChildRunEvent.Parked);
            journal.HoldNext(static evt => evt is ChildRunEvent.Parked);
        }
        var run = await StartAsync(id, $"park-{row}");
        ToolInteractionRequest? prompt = null;
        if (row == "batch")
            await siblingStarted.WaitAsync(Deadline, Cancellation);
        else if (row == "park-rejected")
            await journal.HoldEntered.Task.WaitAsync(Deadline, Cancellation);
        else
        {
            prompt = (await AwaitPromptsAsync(run.Run.RunId, 1))[0];
            await AssertParkedAsync(journal, id, run, 1);
        }
        if (row == "restart")
        {
            await RestartSessionAsync(id);
            await JoinRecorderAsync(id);
        }

        // Act: the cancel goes into the session mailbox before the journal answers a held write.
        var session = await ResolveSessionAsync(id);
        var cancel = session.Ask<string>(new ChildRunControl(run.Run.RunId, true, run.Run.OriginalContext), Deadline, Cancellation);
        journal.ReleaseHold.TrySetResult();
        await cancel;
        Sibling.Release();

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: 0);
        Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Result.Completion);
        if (row == "batch")
            Assert.Empty(RequestsOf(journal, run.Run.RunId));
        if (row == "restart")
        {
            Assert.IsType<CommandNack>(await AnswerAsync(id, prompt!.CallId, Alice));
            await AwaitAssertAsync(() => Assert.Contains(Outputs<TextOutput>(),
                output => output.Text == ChildWithdrawalTexts.For(WithdrawReason.CancelledByParent)), Deadline,
                cancellationToken: Cancellation);
        }
    }

    /// <summary>
    /// S4, restart (PR 2: the park is durable in the run record). Rows:
    /// <list type="bullet">
    /// <item>drain: Alice answers during a restart drain. Recovery resumes the run, and the call runs once.</item>
    /// <item>passivating: an answer during the restart passivation records nothing (the restart nack, or no reply when
    /// the session stopped first). The prompt stays open, and the answer after the restart runs the call once.</item>
    /// <item>compaction: Alice answers p1. The passivation snapshot and its journal delete run. After the wake, Alice
    /// answers p2, and both calls run once.</item>
    /// <item>crash-answered: the session stops after the last answer and before the Resumed record. Recovery resumes.</item>
    /// <item>crash-resumed: the session stops after the Resumed record. Recovery records the run as lost, and the call
    /// does not run again.</item>
    /// <item>crash-notice: the session crashes while the closing notice waits in its turn, and no request is open.
    /// Recovery closes the notice with no turn, and it admits no new notice for the empty open set.</item>
    /// <item>wrong-user: Bob answers after the restart. He gets the nack, and nothing persists.</item>
    /// <item>damaged: the stored park has EqualRounds = 7. Recovery completes, and the prompt stays open. The answer
    /// ends the run with park_damaged, and the call does not run.</item>
    /// <item>unbound: the stored park has a shell request whose call is not in the park transcript, so no one-time
    /// consent can bind its shell source (#2665). The answer ends the run with park_damaged, no call runs, and a late
    /// click does not get "already answered".</item>
    /// <item>counter: the resumed child parks again after a restart. No prompt of the second park reuses a call ID.</item>
    /// <item>late-click: after the resume, the end, and a snapshot, a click on the old prompt gets the closed-call reply.
    /// Nothing runs again.</item>
    /// <item>wake: the session passivates, and Alice's click itself wakes it. The journal holds the replay, so the click
    /// surely waits in the stash and runs before the recovery of the child runs. The child resumes once and is not
    /// recorded as lost.</item>
    /// </list>
    /// Planted bug: a restart loses the park, a parked session cannot passivate, an answer during a drain or a crash is
    /// lost, a snapshot drops the first answer, a crash after Resumed runs the call again, the counter starts again, or a
    /// damaged park stops the recovery.
    /// </summary>
    [Theory]
    [InlineData("drain")]
    [InlineData("passivating")]
    [InlineData("compaction")]
    [InlineData("crash-answered")]
    [InlineData("crash-resumed")]
    [InlineData("crash-notice")]
    [InlineData("wrong-user")]
    [InlineData("damaged")]
    [InlineData("unbound")]
    [InlineData("counter")]
    [InlineData("late-click")]
    [InlineData("wake")]
    public async Task A_restart_keeps_a_durable_park(string row)
    {
        // Arrange
        var id = new SessionId($"park/restart-{row}");
        var journal = await JournalAsync();
        ScriptParent();
        PlanChild($"park-{row}", row switch
        {
            "compaction" => [[Probe("p1"), Probe("p2")]],
            "counter" => [[Probe("p1")], [Probe("p2")]],
            // The resumed child runs a second round with a tool that the row holds.
            "crash-resumed" or "crash-notice" => [[Probe("p1")], [SiblingCall("s2")]],
            _ => [[Probe("p1")]]
        });
        if (row == "damaged")
            journal.RewriteNextStored(static evt => evt is ChildRunEvent.Parked parked ? WithEqualRounds(parked, 7) : null);
        if (row == "unbound")
            journal.RewriteNextStored(static evt => evt is ChildRunEvent.Parked parked ? WithoutTheShellCall(parked) : null);
        await JoinRecorderAsync(id);
        var run = await StartAsync(id, $"park-{row}");
        var prompts = await AwaitPromptsAsync(run.Run.RunId, row == "compaction" ? 2 : 1);
        await AssertParkedAsync(journal, id, run, prompts.Length);
        var session = await ResolveSessionAsync(id);
        var stopped = CreateTestProbe();
        stopped.Watch(session);
        var notices = 0;

        // Act: the stop of the session
        switch (row)
        {
            case "wake":
                await PassivateAsync(id);
                break;
            case "drain":
                var childCalls = Clients.A.CallCount;
                var turn = await HoldTurnAsync(journal, id, "Alice keeps the drain waiting.", AliceSource);
                var drain = AskManagerAsync<DaemonRestartPrepared>(new PrepareForDaemonRestart(id, "config-reload"));
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await BarrierAsync(id, run);
                Assert.Empty(ApprovalTool.Contexts);
                Assert.Equal(childCalls, Clients.A.CallCount);
                turn.TrySetResult();
                await drain;
                await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
                break;
            case "passivating":
                // The session is idle. Both messages go straight to the session in order. The answer meets the restart
                // passivation and gets the restart nack, or it meets the stopped session and gets no reply. A restart
                // passivation with no job and no observer ends in one step, so this fixture gives no reply.
                await SettleNoticesAsync(journal, id);
                var replies = CreateTestProbe();
                var restart = session.Ask<DaemonRestartPrepared>(new PrepareForDaemonRestart(id, "config-reload"), Deadline, Cancellation);
                session.Tell(ApproveOnce(id, prompts[0].CallId, Alice), replies.Ref);
                await restart;
                await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
                Assert.Empty(journal.Committed.OfType<ToolApprovalResolved>());
                if (replies.HasMessages)
                    Assert.Equal(SessionIngressGate.RestartInProgressMessage,
                        Assert.IsType<CommandNack>(replies.ReceiveOne(TimeSpan.Zero, Cancellation)).Reason);
                break;
            case "compaction":
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await PassivateAsync(id);
                await AwaitAssertAsync(async () => Assert.True(await LatestSnapshotSequenceNrAsync(id) > 0,
                    "The passivation must save a snapshot."), Deadline, cancellationToken: Cancellation);
                break;
            case "crash-answered":
                // The journal fails the write, so Akka stops the session at that point, as a crash does. The answer
                // writes the closing notice and the Resumed record in one batch, so no notice turn starts.
                await SettleNoticesAsync(journal, id);
                journal.FailMatchingWrites = true;
                journal.RejectWritesOf(static evt => evt is ChildRunEvent.Resumed);
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
                break;
            case "crash-resumed":
                // The resumed child holds its second-round tool until the closing notice turn ends. A persist failure
                // while a turn runs fails that turn, and the row must not test that path. Then the checkpoint write
                // of the second round fails, so Akka stops the session after Resumed, as a crash does.
                var secondRound = Sibling.HoldNext();
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await secondRound.WaitAsync(Deadline, Cancellation);
                await SettleNoticesAsync(journal, id);
                journal.FailMatchingWrites = true;
                journal.RejectWritesOf(static evt => evt is ChildRunEvent.Checkpointed);
                Sibling.Release();
                await stopped.ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
                break;
            case "crash-notice":
                // The resumed child waits in its second round, and the closing notice waits in its model call.
                var secondTool = Sibling.HoldNext();
                var noticeHold = NewGate();
                var noticeEntered = ParentGate.Arm(request => FreshUserTexts(request).Any(text =>
                    text.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)), noticeHold);
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await secondTool.WaitAsync(Deadline, Cancellation);
                await noticeEntered.WaitAsync(Deadline, Cancellation);
                notices = ConsentNotices(journal, id).Count;
                await StopAsync(session);
                noticeHold.TrySetResult();
                Sibling.Release();
                break;
            case "late-click":
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await AwaitAssertAsync(() => Assert.NotEmpty(TerminalsOf(journal, run.Run.RunId)), Deadline,
                    cancellationToken: Cancellation);
                await SettleNoticesAsync(journal, id);
                await PassivateAsync(id);
                break;
            default:
                await RestartSessionAsync(id);
                break;
        }
        if (row != "wake")
            await JoinRecorderAsync(id);

        // Act: after the restart
        var answers = journal.Committed.OfType<ToolApprovalResolved>().Count();
        switch (row)
        {
            case "passivating" or "wrong-user" or "damaged" or "unbound":
                if (row == "wrong-user")
                {
                    Assert.Equal(ApprovalNackReasons.WrongRequester,
                        Assert.IsType<CommandNack>(await AnswerAsync(id, prompts[0].CallId, Bob)).Reason);
                    Assert.Equal(answers, journal.Committed.OfType<ToolApprovalResolved>().Count());
                }
                if (row == "damaged")
                    Assert.Empty(TerminalsOf(journal, run.Run.RunId));
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                break;
            case "compaction":
                AssertAck(await AnswerAsync(id, prompts[1].CallId, Alice));
                break;
            case "counter":
                AssertAck(await AnswerAsync(id, prompts[0].CallId, Alice));
                await AwaitAssertAsync(() => Assert.Equal(2, RequestsOf(journal, run.Run.RunId).Count), Deadline,
                    cancellationToken: Cancellation);
                var callIds = RequestsOf(journal, run.Run.RunId).Select(static request => request.CallId).ToArray();
                Assert.Equal(callIds.Length, callIds.Distinct(StringComparer.Ordinal).Count());
                AssertAck(await AnswerAsync(id, new ToolCallId(callIds[1]), Alice));
                break;
            case "wake":
                // The journal holds the replay of the woken session, so the join and the click wait in its stash.
                journal.HoldNext(evt => evt is JournalReplay replay && replay.PersistenceId == $"session-{id.Value}");
                var joined = JoinRecorderAsync(id);
                await journal.HoldEntered.Task.WaitAsync(Deadline, Cancellation);
                var woken = await ResolveSessionAsync(id);
                var click = woken.Ask<ISessionResponse>(ApproveOnce(id, prompts[0].CallId, Alice), Deadline, Cancellation);
                journal.ReleaseHold.TrySetResult();
                AssertAck(await click);
                await joined;
                break;
            case "crash-notice":
                // The open set is empty, so recovery closes the waiting notice and admits no new one.
                await BarrierAsync(id, run);
                var waiting = ConsentNotices(journal, id)[notices - 1];
                Assert.Contains(journal.Committed.OfType<InputClosed>(), closed => closed.InputIds.Contains(waiting.InputId));
                Assert.Equal(notices, ConsentNotices(journal, id).Count);
                break;
            case "late-click":
                Assert.Equal(ApprovalNackReasons.PromptExpired,
                    Assert.IsType<CommandNack>(await AnswerAsync(id, prompts[0].CallId, Alice)).Reason);
                Assert.Equal(answers, journal.Committed.OfType<ToolApprovalResolved>().Count());
                break;
        }

        // Assert
        var terminal = await AssertRunInvariantsAsync(journal, id, run, probeRuns: row switch
        {
            "damaged" or "unbound" => 0,
            "compaction" or "counter" => 2,
            _ => 1
        });
        switch (row)
        {
            case "crash-resumed" or "crash-notice":
                Assert.True(terminal.Lost);
                break;
            case "damaged":
                Assert.Equal(SubAgentOutcomeReason.ParkDamaged, terminal.Result.OutcomeReason);
                break;
            case "unbound":
                Assert.Equal(SubAgentOutcomeReason.ParkDamaged, terminal.Result.OutcomeReason);
                Assert.NotEqual(ApprovalNackReasons.AlreadyAnswered,
                    Assert.IsType<CommandNack>(await AnswerAsync(id, prompts[0].CallId, Alice)).Reason);
                break;
            default:
                Assert.True(terminal.Result.Success, terminal.Result.Output);
                break;
        }
    }

    // Two answers of Alice for one prompt. The journal holds the write of the first answer. The second answer goes
    // straight into the session mailbox before the journal rejects the first, so the session takes it first.
    private async Task AnswerTwiceAsync(AcceptanceFault journal, SessionId id, ToolCallId callId)
    {
        var first = AnswerAsync(id, callId, Alice);
        await journal.HoldEntered.Task.WaitAsync(Deadline, Cancellation);
        var session = await ResolveSessionAsync(id);
        var second = session.Ask<ISessionResponse>(ApproveOnce(id, callId, Alice), Deadline, Cancellation);
        journal.ReleaseHold.TrySetResult();
        Assert.Equal(ApprovalNackReasons.PersistFailed, Assert.IsType<CommandNack>(await first).Reason);
        AssertAck(await second);
    }

    // A park whose stored loop guard has an equal-round count that no live loop guard can reach (PR 2 design D7).
    // A park whose request is a shell request, and whose transcript holds no assistant call for it.
    private static ChildRunEvent.Parked WithoutTheShellCall(ChildRunEvent.Parked parked) => parked with
    {
        Park = parked.Park with
        {
            Transcript = [.. parked.Park.Transcript.Where(static message => message.Role != Netclaw.Actors.Protocol.ChatRole.Assistant)]
        },
        Requests = [.. parked.Requests.Select(static request => request with { ToolName = "shell_execute" })]
    };

    private static ChildRunEvent.Parked WithEqualRounds(ChildRunEvent.Parked parked, int equalRounds) => parked with
    {
        Park = parked.Park with
        {
            LoopGuard = parked.Park.LoopGuard with
            {
                Episodes = new Dictionary<ToolRecurrenceKey, ToolRecurrenceEpisode>
                {
                    [new ToolRecurrenceKey(new ToolName("authority_probe"), "planted")] = new("ok", equalRounds, false, false)
                }
            }
        }
    };

    // The parent declares a project directory with a "cd:{path}" message, and the session settles.
    private async Task DeclareProjectAsync(AcceptanceFault journal, SessionId id, string path)
    {
        await SendAsync(id, $"cd:{path}", AliceSource);
        await AwaitAssertAsync(() => Assert.Contains(Clients.Parent.ReceivedMessages, request => Results(request)
            .Any(result => result.Result?.ToString() == $"project {path}")), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, id);
    }

    private void ScriptParentWithProjects() => Clients.Parent.ResponseContentsSelector = static request =>
        FreshUserTexts(request).FirstOrDefault(static text => text.StartsWith("cd:", StringComparison.Ordinal)) is { } cd
            ? HasResult(request, cd) ? null : [ProjectCall(cd, cd[3..])]
            : ScriptedParentReply(request);

    private static FunctionCallContent ProjectCall(string callId, string path) => new(callId, ProjectTool.ToolName,
        new Dictionary<string, object?> { ["Path"] = path, ["_rationale"] = "Declare the project." });

    // A set_working_directory double for the parent and the child. It declares the directory of its argument with no
    // path policy.
    private sealed class ProjectTool : INetclawTool
    {
        public const string ToolName = SetWorkingDirectoryTool.ToolName;
        public string Name => ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Declare the project directory.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create((string Path) => Path, Name, Description);
        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context,
            CancellationToken ct = default)
        {
            var path = arguments?["Path"]?.ToString() ?? throw new InvalidOperationException("The path is required.");
            return Task.FromResult(context.SuccessProject($"project {path}", path));
        }
    }
}
