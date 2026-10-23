// -----------------------------------------------------------------------
// <copyright file="RestartDrainSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Persistence.Journal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using AkkaPersistence = Akka.Persistence.Persistence;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// The restart drain contract (#2601). One harness drives a session to a drain point and sends the drain. One check
/// then proves the same postconditions for each drain point:
/// <list type="bullet">
/// <item>The drain acknowledges before the 20-second coordinator deadline (virtual time).</item>
/// <item>After the drain request, no model call starts and no tool batch starts or runs.</item>
/// <item>The input of the stopped turn stays pending only when the turn resumes and no tool batch consumed it.</item>
/// <item>A resumable turn leaves exactly one pending input. After a tool batch, that input is a continuation input
/// without text that carries the recorded requester of the turn (#2632).</item>
/// <item>A restart reminder exists exactly when an input stays pending or the stopped turn resumes. The reminder carries
/// the recorded audience, boundary, and channel of the turn.</item>
/// <item>When the turn does not resume, exactly one notice with the text for the drain reason posts. Otherwise no
/// notice posts.</item>
/// <item>When the turn resumes, the restart reminder starts the next model call of the turn after the restart. The
/// reminder text is not an input, and the history pairs each tool call with its result. The resumed turn consumes the
/// pending input, and no turn consumes inputs of two requesters (#2632).</item>
/// <item>When the session dropped tool calls, the next model request after the restart has a valid history.</item>
/// </list>
/// One row has a different contract. When a tool still runs at the coordinator deadline, its effect is unknown. The
/// session does not acknowledge, so no restart reminder runs the turn again blindly.
/// A drain point with a child run needs the child fixture. The notice rule for a child-only reminder is a pure decision,
/// so <see cref="RestartNoticeRuleTests"/> covers it.
/// </summary>
public sealed class RestartDrainSpecs : LlmSessionTestBase
{
    private const string SystemPrompt = "You are a test assistant in a tool loop.";
    private const string RequesterId = "U-drain";
    private const string TurnInputText = "Check the package profile";
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CoordinatorDeadline = TimeSpan.FromSeconds(20);

    private readonly ScriptedChatClient _chat = new();
    private readonly HoldingToolExecutor _tools = new();
    private readonly ControllableWorkingContextSnapshotProvider _snapshots = new();
    private readonly FakeJobManager _jobs = new();

    public RestartDrainSpecs(ITestOutputHelper output) : base(output)
    {
    }

    protected override bool UseTestScheduler => true;

    public enum DrainPoint
    {
        FirstModelCall,
        ModelCallAfterToolBatch,
        ModelCallDeepInToolLoop,
        AutomationTurnAfterToolBatch,
        TurnWithoutChannelType,
        ToolBatchBoundary,
        ToolStillRunning,
        LiveBackgroundJobAtDrain,
        SecondDeliveryAfterToolBatch,
        NewInputBeforeReminder,
        AutomaticInputBeforeReminder,
        ContinuationOrphanedThenLaterDrain,
        ContinuationWriteRejected,
        ContinuationWriteFailed,
        GraceToolCalls,
        GraceToolCallsAfterText,
        GraceEmptyReplyRetry,
        ToolLoopCompaction,
        CompactionReplay,
        PreparedModelRequest,
        BufferedInput,
    }

    [Theory]
    [InlineData(DrainPoint.FirstModelCall, "config-reload", true, true)]
    // A tool batch consumed the turn input. The turn resumes from its own recorded authority (#2632).
    [InlineData(DrainPoint.ModelCallAfterToolBatch, "daemon-stop", true, true)]
    [InlineData(DrainPoint.ModelCallDeepInToolLoop, "daemon-stop", true, true)]
    [InlineData(DrainPoint.ToolBatchBoundary, "config-reload", true, true)]
    [InlineData(DrainPoint.ToolLoopCompaction, "config-reload", true, true)]
    [InlineData(DrainPoint.SecondDeliveryAfterToolBatch, "daemon-stop", true, true)]
    [InlineData(DrainPoint.NewInputBeforeReminder, "daemon-stop", true, true)]
    // Any later input closes the continuation, also an automatic one (a reminder fire on the ordinary admission path).
    [InlineData(DrainPoint.AutomaticInputBeforeReminder, "daemon-stop", true, true)]
    [InlineData(DrainPoint.ContinuationOrphanedThenLaterDrain, "daemon-stop", true, true)]
    // The journal does not keep the continuation input, so no reminder can resume the turn. The person gets the notice.
    [InlineData(DrainPoint.ContinuationWriteRejected, "config-reload", false, false)]
    [InlineData(DrainPoint.ContinuationWriteFailed, "daemon-stop", false, false)]
    // The overflow rollback took the tool calls and results of the turn out of the history. The model cannot see what
    // ran, so the turn does not resume.
    [InlineData(DrainPoint.CompactionReplay, "config-reload", false, false)]
    // Passivation stops the live job, and a resume could submit it again. The turn does not resume.
    [InlineData(DrainPoint.LiveBackgroundJobAtDrain, "daemon-stop", false, false)]
    // A reminder turn resumes under its own automation authority, as a person turn does.
    [InlineData(DrainPoint.AutomationTurnAfterToolBatch, "daemon-stop", true, true)]
    // The turn has no channel type, so no reminder can deliver to it. The person gets the notice.
    [InlineData(DrainPoint.TurnWithoutChannelType, "daemon-stop", false, false)]
    // A possible effect blocks the reminder: the tool result is not in the journal.
    [InlineData(DrainPoint.ToolStillRunning, "daemon-stop", false, false)]
    [InlineData(DrainPoint.GraceToolCalls, "config-reload", true, true)]
    [InlineData(DrainPoint.GraceToolCallsAfterText, "config-reload", false, false)]
    [InlineData(DrainPoint.GraceEmptyReplyRetry, "config-reload", true, true)]
    [InlineData(DrainPoint.PreparedModelRequest, "config-reload", true, true)]
    [InlineData(DrainPoint.BufferedInput, "config-reload", false, true)]
    public async Task A_restart_drain_meets_the_drain_contract(
        DrainPoint point,
        string reason,
        bool turnResumes,
        bool reminderExpected)
    {
        // Arrange
        var sessionId = new SessionId($"D-drain/{point}-{reason}");
        var subscriber = CreateTestProbe("drain-subscriber");
        await JoinAsync(subscriber, sessionId);
        var release = await DriveToDrainPointAsync(point, sessionId, subscriber);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var before = new Activity(_chat.MainCalls, _tools.Runs, Count<ToolBatchStarted>(await ReadJournalAsync(sessionId)));

        // Act
        var drain = await RequestDrainAsync(sessionId, reason);
        if (point == DrainPoint.ToolStillRunning)
        {
            await AssertRunningToolHoldsTheDrainAsync(sessionId, drain, before, turnResumes || reminderExpected, release!);
            return;
        }

        if (point == DrainPoint.ContinuationWriteFailed)
        {
            release!();
            await AssertFailedContinuationWritePostsTheNoticeAsync(sessionId, session, subscriber, reason);
            return;
        }

        if (release is not null)
        {
            // The clock must not move before the session handles the released work. Otherwise the model grace stop
            // can win, and the row does not test its drain point.
            var nextWork = Task.WhenAny(drain, _chat.NextCall, _tools.NextRun);
            release();
            try
            {
                await nextWork.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail("After the released work, the drain did not acknowledge and no model call or tool run started.");
            }
        }

        var prepared = await AwaitDrainAsync(drain, before);

        // Assert
        var resumed = await AssertDrainContractAsync(
            sessionId, session, subscriber, prepared, before, reason, turnResumes, reminderExpected);
        if (resumed is not null)
            await AssertTurnResumesAfterRestartAsync(point, reason, sessionId, prepared.RestartReminder!, resumed);
        if (point == DrainPoint.GraceToolCallsAfterText)
            await AssertValidHistoryAfterRestartAsync(sessionId);
        if (point == DrainPoint.LiveBackgroundJobAtDrain)
        {
            await PingAsync(sessionId);
            Assert.True(_jobs.Starts == 1, $"The session submitted {_jobs.Starts} background jobs, but the turn started one.");
        }
    }

    // Drives the session to the drain point. Returns the action that releases held work after the drain request.
    private async Task<Action?> DriveToDrainPointAsync(DrainPoint point, SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        Action? release = null;
        Task? reached = null;
        switch (point)
        {
            case DrainPoint.FirstModelCall:
                _chat.Script(Step.StreamUntilCancelled);
                break;
            case DrainPoint.ModelCallAfterToolBatch or DrainPoint.BufferedInput or DrainPoint.AutomationTurnAfterToolBatch
                or DrainPoint.TurnWithoutChannelType or DrainPoint.SecondDeliveryAfterToolBatch
                or DrainPoint.NewInputBeforeReminder or DrainPoint.AutomaticInputBeforeReminder:
                _chat.Script(Step.ToolCall, Step.StreamUntilCancelled);
                break;
            case DrainPoint.LiveBackgroundJobAtDrain:
                ActorRegistry.Register<BackgroundJobManagerActorKey>(_jobs.Manager(Sys), overwrite: true);
                _chat.Script(Step.ToolCall, Step.BackgroundToolCall, Step.StreamUntilCancelled);
                break;
            case DrainPoint.ModelCallDeepInToolLoop:
                _chat.Script(Step.ToolCall, Step.ToolCall, Step.ToolCall, Step.StreamUntilCancelled);
                break;
            case DrainPoint.ContinuationOrphanedThenLaterDrain:
                await LeaveStaleContinuationAsync(sessionId, subscriber);
                _chat.Script(Step.ToolCall, Step.StreamUntilCancelled);
                break;
            case DrainPoint.ToolBatchBoundary or DrainPoint.ToolStillRunning or DrainPoint.ContinuationWriteRejected
                or DrainPoint.ContinuationWriteFailed:
                if (point is DrainPoint.ContinuationWriteRejected or DrainPoint.ContinuationWriteFailed)
                {
                    var fault = new AcceptanceFault(rejectAcceptance: false, holdAcceptance: false)
                    {
                        FailMatchingWrites = point == DrainPoint.ContinuationWriteFailed
                    };
                    fault.RejectWritesOf(payload => payload is InputAdmitted { UserMessage.Content: "" });
                    await AkkaPersistence.Instance.Apply(Sys).JournalFor(null)
                        .Ask<Done>(fault, Deadline, TestContext.Current.CancellationToken);
                }

                _chat.Script(Step.ToolCall, Step.StreamUntilCancelled);
                release = _tools.HoldNextRun().SetResult;
                reached = _tools.Started.Task;
                break;
            case DrainPoint.GraceToolCalls:
                _chat.Script(Step.HeldToolCall);
                break;
            case DrainPoint.GraceToolCallsAfterText:
                _chat.Script(Step.HeldTextAndToolCall);
                break;
            case DrainPoint.GraceEmptyReplyRetry:
                _chat.Script(Step.HeldThinkingOnly);
                break;
            case DrainPoint.ToolLoopCompaction:
                _chat.Script(Step.ToolCallWithFullContext);
                break;
            case DrainPoint.CompactionReplay:
                // A completed turn gives the compaction enough history for a summary call, which the row holds.
                _chat.Script(Step.Text, Step.ToolCall, Step.ContextOverflow);
                await SendAsync(sessionId, "Earlier question", PersonSource);
                await subscriber.FishForMessageAsync<TurnCompleted>(
                    _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
                break;
            case DrainPoint.PreparedModelRequest:
                _chat.Script(Step.StreamUntilCancelled);
                var inspection = new TaskCompletionSource<WorkingContextSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _snapshots.Pending = inspection;
                reached = _snapshots.InvocationStarted.Task;
                release = () => inspection.SetResult(new WorkingContextSnapshot
                {
                    WorkingContext = WorkingContext.Empty,
                    Git = new GitWorkingContextInspection.Skipped()
                });
                break;
        }

        if (point is DrainPoint.GraceToolCalls or DrainPoint.GraceToolCallsAfterText or DrainPoint.GraceEmptyReplyRetry)
            release = _chat.ReleaseHeldCall;

        if (point is DrainPoint.ToolLoopCompaction or DrainPoint.CompactionReplay)
        {
            reached = _chat.SummaryHeld.Task;
            release = _chat.HoldSummary().SetResult;
        }

        await SendAsync(sessionId, TurnInputText, point switch
        {
            DrainPoint.AutomationTurnAfterToolBatch => AutomationSource,
            DrainPoint.TurnWithoutChannelType => null,
            _ => PersonSource
        });
        await (reached ?? _chat.Reached.Task).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        if (point == DrainPoint.BufferedInput)
            await SendAsync(sessionId, "Also check the beta feed", PersonSource);
        return release;
    }

    private sealed record Activity(int MainCalls, int ToolRuns, int ToolBatches);

    // Sends the drain request. A join after the request proves that the session processed the request.
    private async Task<Task<DaemonRestartPrepared>> RequestDrainAsync(SessionId sessionId, string reason)
    {
        var drain = SessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, reason),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        await PingAsync(sessionId);
        return drain;
    }

    // The session handles the messages in its mailbox before the join.
    private async Task PingAsync(SessionId sessionId)
        => await SessionManager.Ask<SessionJoined>(new JoinSession(CreateTestProbe())
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, Deadline, TestContext.Current.CancellationToken);

    // A tool still runs when the coordinator deadline passes. The drain does not cancel a tool that does not wait for
    // an approval, so the effect of the tool is unknown and the journal has no result for it. The session does not
    // acknowledge. The daemon then gets no restart reminder, and its shutdown alert names the session
    // (DaemonRestartCoordinatorTests). The release lets the session end the batch after the check. In production the
    // daemon ignores that late acknowledgement.
    // A drain stops another person's turn after a tool batch, and its reminder never arrives. The continuation input of
    // that turn stays pending. After the restart, a person turn starts and ends. The later drain must then decide for
    // the current turn only, never for the stale one.
    private async Task LeaveStaleContinuationAsync(SessionId sessionId, Akka.TestKit.TestProbe subscriber)
    {
        _chat.Script(Step.ToolCall, Step.StreamUntilCancelled);
        await SendAsync(sessionId, "Check the old profile", OtherPersonSource);
        await _chat.Reached.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        var stale = await AwaitDrainAsync(
            await RequestDrainAsync(sessionId, "daemon-stop"), new Activity(_chat.MainCalls, _tools.Runs, 0));
        Assert.NotNull(stale.RestartReminder);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<SessionDeactivated>(
            _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);

        await JoinAsync(subscriber, sessionId);
        _chat.Script();
        await SendAsync(sessionId, "Earlier question", PersonSource);
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
    }

    // The journal fails the write of the continuation input, so Akka stops the session. The person must get the notice
    // before the session stops. No input stays pending, so nothing resumes later.
    private async Task AssertFailedContinuationWritePostsTheNoticeAsync(
        SessionId sessionId, IActorRef session, Akka.TestKit.TestProbe subscriber, string reason)
    {
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);
        var notices = 0;
        while (subscriber.HasMessages)
        {
            if (subscriber.ReceiveOne(TimeSpan.Zero) is TextOutput text && text.Text == RestartNoticeRuleTests.ExpectedNotice(reason))
                notices++;
        }

        Assert.True(notices == 1, $"The session posted {notices} restart notices before it stopped.");
        Assert.Empty(PendingInputIds(await ReadJournalAsync(sessionId)));
    }

    private async Task AssertRunningToolHoldsTheDrainAsync(
        SessionId sessionId,
        Task<DaemonRestartPrepared> drain,
        Activity before,
        bool resumeExpected,
        Action release)
    {
        Assert.False(resumeExpected, "A turn with a tool of unknown effect must not resume.");
        AdvanceScheduler(CoordinatorDeadline);
        await PingAsync(sessionId);
        Assert.False(drain.IsCompleted, "The drain acknowledged while a tool still ran.");
        AssertNoWorkAfterDrain(before);
        var events = await ReadJournalAsync(sessionId);
        Assert.Single(events.OfType<ToolBatchStarted>());
        Assert.Empty(events.OfType<ToolCallRecorded>());
        release();
    }

    // Moves the virtual clock in 3-second steps, but not past the coordinator deadline.
    private async Task<DaemonRestartPrepared> AwaitDrainAsync(Task<DaemonRestartPrepared> drain, Activity before)
    {
        var moved = TimeSpan.Zero;
        await AwaitAssertAsync(
            () =>
            {
                AssertNoWorkAfterDrain(before);
                if (!drain.IsCompleted && moved + TimeSpan.FromSeconds(3) < CoordinatorDeadline)
                {
                    AdvanceScheduler(TimeSpan.FromSeconds(3));
                    moved += TimeSpan.FromSeconds(3);
                }

                Assert.True(drain.IsCompleted,
                    $"The drain did not acknowledge before the coordinator deadline (virtual time moved {moved.TotalSeconds}s).");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        return await drain;
    }

    private void AssertNoWorkAfterDrain(Activity before)
    {
        Assert.True(_chat.MainCalls == before.MainCalls,
            $"The session started model call {_chat.MainCalls} after the drain request.");
        Assert.True(_tools.Runs == before.ToolRuns, "The session ran a tool after the drain request.");
    }

    // The shared postconditions of every drain point. Returns the pending input that the restart reminder resumes.
    private async Task<InputAdmitted?> AssertDrainContractAsync(
        SessionId sessionId,
        IActorRef session,
        Akka.TestKit.TestProbe subscriber,
        DaemonRestartPrepared prepared,
        Activity before,
        string reason,
        bool turnResumes,
        bool reminderExpected)
    {
        AssertNoWorkAfterDrain(before);
        var notices = new List<string>();
        await subscriber.FishForMessageAsync(
            message =>
            {
                if (message is TextOutput text)
                    notices.Add(text.Text);
                return message is SessionDeactivated;
            },
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);

        var events = await ReadJournalAsync(sessionId);
        Assert.True(Count<ToolBatchStarted>(events) == before.ToolBatches,
            "The session recorded a tool batch after the drain request.");
        var pending = PendingInputIds(events);
        var turnInput = events.OfType<InputAdmitted>().Single(input => input.UserMessage.Content == TurnInputText);
        var consumedByBatch = events.OfType<ToolBatchStarted>().Any(batch => batch.ConsumedInputIds.Contains(turnInput.InputId));
        Assert.True(pending.Contains(turnInput.InputId) == (turnResumes && !consumedByBatch),
            "The turn input must stay pending exactly when the turn resumes and no tool batch consumed it.");
        Assert.True(reminderExpected == (pending.Count > 0 || turnResumes),
            "A restart reminder must exist exactly when an input stays pending or the stopped turn resumes.");
        Assert.True(reminderExpected == (prepared.RestartReminder is not null),
            $"The drain returned {(prepared.RestartReminder is null ? "no" : "a")} restart reminder.");
        if (prepared.RestartReminder is { } reminder)
        {
            // The reminder carries exactly the recorded authority of the turn, not a session default (#2632).
            var authority = turnInput.TurnContext;
            Assert.Equal(SessionExecutionLifetime.RestartReminderInstruction, reminder.Instructions);
            Assert.Equal(DeliveryKind.CurrentSession, reminder.Delivery.Kind);
            Assert.Equal(sessionId.Value, reminder.Delivery.SessionId);
            Assert.Equal(authority.ChannelType, reminder.Delivery.OriginChannelType?.ToWireValue());
            Assert.Equal(authority.Audience, reminder.Audience);
            Assert.Equal(authority.Boundary, reminder.Boundary);
        }

        if (!turnResumes)
        {
            Assert.Equal(RestartNoticeRuleTests.ExpectedNotice(reason), Assert.Single(notices));
            return null;
        }

        Assert.Empty(notices);
        var resumable = events.OfType<InputAdmitted>().Where(input => pending.Contains(input.InputId)).ToArray();
        Assert.True(resumable.Length == 1,
            $"A resumable turn must leave exactly one pending input, but {resumable.Length} stay pending.");
        var resumed = resumable[0];
        Assert.Equal(Requester(turnInput.TurnContext), Requester(resumed.TurnContext));
        if (consumedByBatch)
        {
            Assert.True(string.IsNullOrEmpty(resumed.UserMessage.Content) && string.IsNullOrEmpty(resumed.ExecutableText),
                "The continuation input of a stopped turn must carry no new text.");
        }

        return resumed;
    }

    // The recorded requester of an input. Two inputs of one turn must have the same requester.
    private static (string? Sender, PrincipalClassification? Principal, TrustAudience Audience, TrustBoundary? Boundary,
        string? Channel) Requester(TurnContextRecord context)
        => (context.RequesterSenderId?.Value, context.RequesterPrincipal, context.Audience, context.Boundary,
            context.ChannelType);

    // After the restart, the first model request must pair each tool call with its result.
    private async Task AssertValidHistoryAfterRestartAsync(SessionId sessionId)
    {
        var subscriber = CreateTestProbe("after-restart-subscriber");
        await JoinAsync(subscriber, sessionId);
        var calls = _chat.MainCalls;
        await SendAsync(sessionId, "Continue the work", PersonSource);
        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
        AssertPairedToolHistory(_chat.MainRequests[calls]);
    }

    // After the restart, the reminder system sends the restart reminder. It must start the next model call of the
    // stopped turn under the recorded requester of that turn. The reminder is a signal, not an input: its text never
    // reaches the model, and the session records no input for it (#2632).
    private async Task AssertTurnResumesAfterRestartAsync(
        DrainPoint point, string reason, SessionId sessionId, ReminderDefinition reminder, InputAdmitted resumed)
    {
        _chat.Script(point == DrainPoint.SecondDeliveryAfterToolBatch ? [Step.ToolCall, Step.StreamUntilCancelled] : []);
        var subscriber = CreateTestProbe("resume-subscriber");
        await JoinAsync(subscriber, sessionId);
        if (point is DrainPoint.NewInputBeforeReminder or DrainPoint.AutomaticInputBeforeReminder)
        {
            await AssertLaterInputClosesTheContinuationAsync(sessionId, subscriber, reason, reminder, resumed,
                point == DrainPoint.NewInputBeforeReminder ? OtherPersonSource : LaterFireSource);
            return;
        }

        var calls = _chat.MainCalls;
        var reply = await SendRestartReminderAsync(sessionId, reminder);
        Assert.True(reply is CommandAck and not RestartResumeNotNeeded,
            $"The restart reminder did not resume the stopped turn. The session replied {reply}.");
        if (point == DrainPoint.SecondDeliveryAfterToolBatch)
        {
            await AssertSecondDeliveryResumesNothingAsync(sessionId, reminder, calls);
        }
        else
        {
            await subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, Deadline, cancellationToken: TestContext.Current.CancellationToken);
            var request = _chat.MainRequests[calls];
            Assert.DoesNotContain(request, message => message.Text?.Contains(reminder.Instructions, StringComparison.Ordinal) == true);
            // The summary of a tool-loop compaction replaced the turn input. Each other row holds it exactly once.
            var turnInputCopies = request.Count(message => message.Role == AiChatRole.User && message.Text == TurnInputText);
            Assert.True(turnInputCopies == (point == DrainPoint.ToolLoopCompaction ? 0 : 1),
                $"The resumed model request holds {turnInputCopies} copies of the turn input.");
            AssertPairedToolHistory(request);
        }

        var consumers = AssertNoTurnMixesRequesters(await ReadJournalAsync(sessionId), reminder);
        Assert.True(consumers.Any(ids => ids.Contains(resumed.InputId)),
            "The resumed turn did not consume the input that the drain left pending.");
    }

    // Another input arrives before the reminder: a message of another person, or an automatic input. Its admission
    // closes the continuation input and posts the restart notice that the drain held back. The later turn runs under
    // its own authority only, and the reminder that arrives after it resumes nothing (#2632).
    private async Task AssertLaterInputClosesTheContinuationAsync(
        SessionId sessionId,
        Akka.TestKit.TestProbe subscriber,
        string reason,
        ReminderDefinition reminder,
        InputAdmitted resumed,
        MessageSource laterSource)
    {
        var calls = _chat.MainCalls;
        await SendAsync(sessionId, OtherInputText, laterSource);
        var notices = 0;
        await subscriber.FishForMessageAsync(
            message =>
            {
                // The new incarnation of the session may not know the reason of the drain, so each notice text counts.
                if (message is TextOutput text && (text.Text == RestartNoticeRuleTests.ExpectedNotice(reason)
                                                   || text.Text == RestartNoticeRuleTests.ExpectedNotice("config-reload")))
                    notices++;
                return message is TurnCompleted;
            },
            Deadline,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(notices == 1, $"The later input posted {notices} restart notices for the stopped turn.");

        var reply = await SendRestartReminderAsync(sessionId, reminder);
        Assert.True(reply is RestartResumeNotNeeded,
            $"The reminder after the later turn must resume nothing. The session replied {reply}.");
        await PingAsync(sessionId);
        Assert.True(_chat.MainCalls == calls + 1, $"The session made {_chat.MainCalls - calls} model calls after the restart.");
        var request = _chat.MainRequests[calls];
        Assert.True(request[^1].Role == AiChatRole.User && request[^1].Text == OtherInputText,
            "The model request of the later turn must end with the later input.");

        var events = await ReadJournalAsync(sessionId);
        Assert.Contains(events.OfType<InputClosed>(), close => close.InputIds.Contains(resumed.InputId));
        var consumers = AssertNoTurnMixesRequesters(events, reminder);
        Assert.DoesNotContain(consumers, ids => ids.Contains(resumed.InputId));
        var later = Assert.Single(events.OfType<InputAdmitted>(), input => input.UserMessage.Content == OtherInputText);
        Assert.Equal(laterSource.SenderId.Value, later.TurnContext.RequesterSenderId?.Value);
        Assert.Contains(consumers, ids => ids.Contains(later.InputId));
        Assert.Empty(PendingInputIds(events));
    }

    // The resumed turn starts a tool batch, which consumes the pending input. The session actor then stops without a
    // drain and recovers from the journal. A second delivery of the same reminder finds nothing to resume.
    private async Task AssertSecondDeliveryResumesNothingAsync(SessionId sessionId, ReminderDefinition reminder, int calls)
    {
        await AwaitAssertAsync(
            () => Assert.Equal(calls + 2, _chat.MainCalls), Deadline, cancellationToken: TestContext.Current.CancellationToken);
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        Sys.Stop(session);
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: TestContext.Current.CancellationToken);

        var again = await SendRestartReminderAsync(sessionId, reminder);
        Assert.True(again is RestartResumeNotNeeded,
            $"The second delivery of the restart reminder must resume nothing. The session replied {again}.");
        await PingAsync(sessionId);
        Assert.Equal(calls + 2, _chat.MainCalls);
    }

    // No journal record consumes the inputs of two requesters, and the reminder text is never an input. Returns the
    // input IDs of each record that consumes inputs, in journal order.
    private static List<IReadOnlyList<InputId>> AssertNoTurnMixesRequesters(IReadOnlyList<object> events, ReminderDefinition reminder)
    {
        Assert.DoesNotContain(events.OfType<InputAdmitted>(), input => input.UserMessage.Content == reminder.Instructions);
        var admitted = events.OfType<InputAdmitted>().ToDictionary(input => input.InputId);
        var consumers = events
            .Select(payload => payload switch
            {
                TurnRecorded turn => turn.ConsumedInputIds,
                ToolBatchStarted batch => batch.ConsumedInputIds,
                _ => []
            })
            .Where(ids => ids.Count > 0)
            .ToList();
        Assert.All(consumers, ids => Assert.Single(ids.Select(id => Requester(admitted[id].TurnContext)).Distinct()));
        return consumers;
    }

    // After a daemon restart the reminder system sends the restart reminder to the recovered session.
    private async Task<ISessionResponse> SendRestartReminderAsync(SessionId sessionId, ReminderDefinition reminder)
        => await SessionManager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = reminder.Instructions,
            Source = ApprovalWaitTestBase.RestartReminderSource(reminder)
        }, Deadline, TestContext.Current.CancellationToken);

    private static void AssertPairedToolHistory(IReadOnlyList<ChatMessage> request)
    {
        var contents = request.SelectMany(message => message.Contents).ToArray();
        Assert.Equal(
            contents.OfType<FunctionCallContent>().Select(call => call.CallId).Order(),
            contents.OfType<FunctionResultContent>().Select(result => result.CallId).Order());
    }

    // The fault journal behaves as the memory journal until a row installs a fault.
    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore();
        builder.AddHocon($"""
            akka.persistence.journal.inmem.class = "{typeof(AcceptanceBoundaryJournal).AssemblyQualifiedName}"
            """, HoconAddMode.Prepend);
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chat));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            // The spec controls passivation. An idle timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
                // A short history still reaches the summary call, so a row can hold a compaction open.
                KeepRecentMessages = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(SystemPrompt));
        services.AddSingleton<IToolExecutor>(_tools);
        services.AddSingleton<IWorkingContextSnapshotProvider>(_snapshots);
        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    private IActorRef SessionManager => ActorRegistry.Get<SessionManagerActorKey>();

    private async Task JoinAsync(Akka.TestKit.TestProbe subscriber, SessionId sessionId)
    {
        await SessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, Deadline, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
        => await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(Deadline, TestContext.Current.CancellationToken);

    // A null source is a turn without a recorded channel type.
    private async Task SendAsync(SessionId sessionId, string content, MessageSource? source)
        => await SessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = source
        }, Deadline, TestContext.Current.CancellationToken);

    // The boundary is not the default of the audience or the session, so a resume under another authority fails.
    private static readonly MessageSource PersonSource = new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(RequesterId),
        Audience = TrustAudience.Team,
        Boundary = new TrustBoundary("slack:C-drain"),
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };

    private const string OtherInputText = "What is the status of the beta feed?";
    private static readonly MessageSource OtherPersonSource = PersonSource with { SenderId = new SenderId("U-other") };

    // Mirrors the source that ReminderExecutionActor builds for a reminder fire in an existing Slack session.
    private const string AutomationFireKey = "daily-check:1792777119000";
    private static readonly MessageSource AutomationSource = new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId("reminder-system"),
        MessageId = AutomationFireKey,
        TurnId = new TurnId(AutomationFireKey),
        ReminderId = new ReminderId(AutomationFireKey),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("reminder")
        },
        ReceivedAt = FixedReceivedAt,
    };

    // A later fire of another reminder. Static fields start in text order, so this field follows AutomationSource.
    private const string LaterFireKey = "status-check:1792777200000";
    private static readonly MessageSource LaterFireSource = AutomationSource with
    {
        MessageId = LaterFireKey,
        TurnId = new TurnId(LaterFireKey),
        ReminderId = new ReminderId(LaterFireKey)
    };

    private static int Count<T>(IEnumerable<object> events) => events.OfType<T>().Count();

    private static IReadOnlyList<InputId> PendingInputIds(IReadOnlyList<object> events)
    {
        var closed = new HashSet<InputId>();
        foreach (var payload in events)
        {
            switch (payload)
            {
                case TurnRecorded turn:
                    closed.UnionWith(turn.ConsumedInputIds);
                    break;
                case ToolBatchStarted batch:
                    closed.UnionWith(batch.ConsumedInputIds);
                    break;
                case InputClosed input:
                    closed.UnionWith(input.InputIds);
                    break;
            }
        }

        return events.OfType<InputAdmitted>().Select(admitted => admitted.InputId).Where(id => !closed.Contains(id)).ToArray();
    }

    private async Task<IReadOnlyList<object>> ReadJournalAsync(SessionId sessionId)
    {
        var persistenceId = $"session-{sessionId.Value}";
        var probe = CreateTestProbe("journal-reader-" + Guid.NewGuid().ToString("N"));
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var payloads = new List<object>();
        while (true)
        {
            switch (await probe.ExpectMsgAsync<object>(Deadline, cancellationToken: TestContext.Current.CancellationToken))
            {
                case MemoryJournal.ReplayedEvent replayed when replayed.Persistent.PersistenceId == persistenceId:
                    payloads.Add(replayed.Persistent.Payload);
                    break;
                case MemoryJournal.EventReplaySuccess:
                    return payloads;
                case MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The journal replay failed.", failure.Cause);
            }
        }
    }

    private enum Step
    {
        ToolCall,
        BackgroundToolCall,
        ToolCallWithFullContext,
        StreamUntilCancelled,
        HeldToolCall,
        HeldTextAndToolCall,
        HeldThinkingOnly,
        ContextOverflow,
        Text,
    }

    /// <summary>
    /// Main model calls follow the script. A call after the script returns a short text reply. A call without the
    /// session system prompt is a sidecar call. The compaction summary call can wait for a gate.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _mainRequests = [];
        private Step[] _script = [];
        private int _mainCalls;
        private int _scriptStart;
        private TaskCompletionSource _nextCall = NewGate();
        private TaskCompletionSource? _heldCallRelease;
        private TaskCompletionSource? _summaryRelease;

        public int MainCalls => Volatile.Read(ref _mainCalls);
        public Task NextCall => Volatile.Read(ref _nextCall).Task;
        // The session reached the drain point: a held call waits, or a call streams until it is cancelled.
        public TaskCompletionSource Reached { get; private set; } = NewGate();
        public TaskCompletionSource SummaryHeld { get; } = NewGate();

        public IReadOnlyList<IReadOnlyList<ChatMessage>> MainRequests
        {
            get { lock (_mainRequests) { return _mainRequests.ToArray(); } }
        }

        // The script starts at the next main model call.
        public void Script(params Step[] steps)
        {
            _scriptStart = MainCalls;
            _script = steps;
            Reached = NewGate();
        }

        public void ReleaseHeldCall() => Volatile.Read(ref _heldCallRelease)?.TrySetResult();

        public TaskCompletionSource HoldSummary()
        {
            var release = NewGate();
            Volatile.Write(ref _summaryRelease, release);
            return release;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => StreamAsync(messages.ToList(), cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!HasSystemText(messages, SystemPrompt))
            {
                if (HasSystemText(messages, "You are a session summarizer")
                    && Interlocked.Exchange(ref _summaryRelease, null) is { } summary)
                {
                    SummaryHeld.TrySetResult();
                    await summary.Task.WaitAsync(cancellationToken);
                }

                yield return new ChatResponseUpdate(AiChatRole.Assistant, "ok");
                yield break;
            }

            lock (_mainRequests)
                _mainRequests.Add(messages);
            var call = Interlocked.Increment(ref _mainCalls);
            Interlocked.Exchange(ref _nextCall, NewGate()).TrySetResult();
            var index = call - _scriptStart;
            var step = index >= 1 && index <= _script.Length ? _script[index - 1] : (Step?)null;

            if (step is Step.HeldTextAndToolCall)
            {
                // The session holds back the first text delta of a call, so the text streams as two deltas.
                yield return new ChatResponseUpdate(AiChatRole.Assistant, "Let me fetch ");
                yield return new ChatResponseUpdate(AiChatRole.Assistant, "the profile.");
            }

            if (step is Step.HeldToolCall or Step.HeldTextAndToolCall or Step.HeldThinkingOnly)
            {
                var release = NewGate();
                Volatile.Write(ref _heldCallRelease, release);
                Reached.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            switch (step)
            {
                case Step.ToolCall or Step.HeldToolCall or Step.HeldTextAndToolCall:
                    yield return ToolCallUpdate(call, usage: null);
                    yield break;
                case Step.BackgroundToolCall:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant,
                    [
                        new FunctionCallContent($"call-drain-{call}", "shell_execute", new Dictionary<string, object?>
                        {
                            ["command"] = "python3 serve_profile.py",
                            ["_background"] = true,
                            ["_rationale"] = "serve the profile"
                        })
                    ]) { FinishReason = ChatFinishReason.ToolCalls };
                    yield break;
                case Step.ToolCallWithFullContext:
                    yield return ToolCallUpdate(call, new UsageDetails
                    {
                        InputTokenCount = 200_000, OutputTokenCount = 1, TotalTokenCount = 200_001
                    });
                    yield break;
                case Step.HeldThinkingOnly:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, [new TextReasoningContent("Thinking only.")])
                    {
                        FinishReason = ChatFinishReason.Stop
                    };
                    yield break;
                case Step.ContextOverflow:
                    throw new InvalidOperationException("The request exceeds the maximum context length of the model.");
                case Step.StreamUntilCancelled:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, [new TextReasoningContent("The card is")]);
                    Reached.TrySetResult();
                    var stopped = NewGate();
                    using (cancellationToken.Register(() => stopped.TrySetCanceled(cancellationToken)))
                        await stopped.Task;
                    yield break;
                default:
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
                    yield break;
            }
        }

        // Each model call gets its own tool call ID, so a loop of several batches has a valid history.
        private static ChatResponseUpdate ToolCallUpdate(int call, UsageDetails? usage)
        {
            List<AIContent> contents =
            [
                new FunctionCallContent($"call-drain-{call}", "shell_execute",
                    new Dictionary<string, object?> { ["command"] = $"python3 fetch_profile.py --page {call}" })
            ];
            if (usage is not null)
                contents.Add(new UsageContent(usage));
            return new ChatResponseUpdate(AiChatRole.Assistant, contents) { FinishReason = ChatFinishReason.ToolCalls };
        }

        private static bool HasSystemText(IReadOnlyList<ChatMessage> messages, string text)
            => messages.Any(message => message.Role == AiChatRole.System
                                       && message.Text?.Contains(text, StringComparison.Ordinal) == true);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Runs each tool call at once. After <see cref="HoldNextRun"/>, the next run waits for the gate.</summary>
    private sealed class HoldingToolExecutor : IToolExecutor
    {
        private TaskCompletionSource? _hold;
        private TaskCompletionSource _nextRun = NewGate();
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);
        public Task NextRun => Volatile.Read(ref _nextRun).Task;
        public TaskCompletionSource Started { get; } = NewGate();

        public TaskCompletionSource HoldNextRun()
        {
            var hold = NewGate();
            Volatile.Write(ref _hold, hold);
            return hold;
        }

        public Task<ShellProcessLaunch> PrepareShellLaunchAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(new ShellProcessLaunch(
                ToolArgumentHelper.GetString(toolCall.Arguments, "Command")!,
                context.ResolveShellCwd(ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory"))
                    ?? throw new InvalidOperationException("The test launch requires a working directory."),
                context.Invocation,
                new Netclaw.Security.ShellCommandPolicy(TestShellEnvironment.Current),
                new Netclaw.Security.ToolPathPolicy(TestShellEnvironment.Current, []),
                static _ => Task.CompletedTask));

        public async Task<string> ExecuteAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            Interlocked.Exchange(ref _nextRun, NewGate()).TrySetResult();
            Started.TrySetResult();
            if (Interlocked.Exchange(ref _hold, null) is { } hold)
                await hold.Task;
            return "Exit code: 0";
        }
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts each background job at once and acknowledges each reap. It counts the job submissions.</summary>
    private sealed class FakeJobManager
    {
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public IActorRef Manager(ActorSystem system) => system.ActorOf(Props.Create(() => new Actor(this)));

        private sealed class Actor : ReceiveActor
        {
            public Actor(FakeJobManager owner)
            {
                Receive<BackgroundJobProtocol.StartBackgroundJob>(_ => Sender.Tell(new BackgroundJobProtocol.BackgroundJobStarted(
                    new BackgroundJobId($"job-{Interlocked.Increment(ref owner._starts)}"))));
                Receive<BackgroundJobProtocol.KillJobsForSession>(kill =>
                    Sender.Tell(new BackgroundJobProtocol.SessionJobsReaped(kill.SessionId, 1)));
            }
        }
    }
}

/// <summary>
/// The notice rule of a restart drain (#2601), as a pure decision. The stopped turn resumes only when the restart
/// reminder replays the inputs of that turn. A reminder that only reports a child run, or that only runs a buffered
/// input as a new turn, does not resume the stopped turn.
/// </summary>
public sealed class RestartNoticeRuleTests
{
    private const string Tail = "The turn did not finish, and it will not continue by itself. Send a message to continue.";

    internal static string ExpectedNotice(string reason) => reason switch
    {
        "daemon-stop" => $"Netclaw stopped this turn because the daemon is stopping. {Tail}",
        "config-reload" => $"Netclaw stopped this turn because the daemon is restarting. {Tail}",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    [Theory]
    // The reminder replays the pending input of the turn, so the turn resumes.
    [InlineData("Eligible", 1, "config-reload", false)]
    // A child-only or buffered-input reminder: no input of the turn stays pending.
    [InlineData("Eligible", 0, "config-reload", true)]
    // No reminder: a tool batch or streamed text closed the input.
    [InlineData("None", 0, "daemon-stop", true)]
    [InlineData("EmittedText", 1, "config-reload", true)]
    public void The_notice_posts_exactly_when_the_stopped_turn_does_not_resume(
        string eligibility,
        int pendingTurnInputCount,
        string reason,
        bool noticeExpected)
    {
        var notice = SessionExecutionLifetime.RestartStoppedTurnNotice(
            Enum.Parse<ReminderEligibility>(eligibility), pendingTurnInputCount, reason);

        Assert.Equal(noticeExpected ? ExpectedNotice(reason) : null, notice);
    }
}
