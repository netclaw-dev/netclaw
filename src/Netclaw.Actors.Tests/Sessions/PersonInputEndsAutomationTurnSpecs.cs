// -----------------------------------------------------------------------
// <copyright file="PersonInputEndsAutomationTurnSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// A person's input ends a running automation turn at the next safe boundary: a tool-batch end, or the end of a
/// compaction that the tool loop started (#2633).
/// <para>
/// Live failure on 0.28.0-beta.1 (session D0AC6CKBK5K/1792598500.645089): a reminder turn ran a long tool loop. The
/// operator sent seven messages, one of them "cancel your reminder and stop doing stuff". Each message waited in the
/// buffer, because a person's input never joins an automation turn. The reminder turn ran about ten more tool rounds
/// over five minutes before the messages ran.
/// </para>
/// <para>
/// Rule: when the running turn is automation (a reminder fire or an approval notice) and a person's input waits that
/// cannot join it, the turn ends after the current tool batch. The turn starts no further model call. The person's
/// input then starts its own turn under its own recorded authority. The ended turn completes quietly, so the channel
/// posts no empty-turn fallback, and a reminder fire records success. The person's first model request holds the
/// history of the ended turn, then one fixed stop note in the per-turn system context, and then the person's message.
/// </para>
/// <para>
/// Compaction (#2633): a compaction that is due at the ending tool batch commits after the close and before the
/// person's first model call, so that call cannot overflow. A person who writes during a tool-loop compaction ends the
/// turn at the compaction end: the automation turn makes no model call after the compaction.
/// </para>
/// <para>
/// Rows that do not change: a person's input waits behind the turn of another person, and the same requester's input
/// joins the running turn. Automation input of the same requester joins an automation turn. The pure decision has its
/// table in <c>ChildConsentPauseMutationTests</c>.
/// </para>
/// </summary>
public sealed class PersonInputEndsAutomationTurnSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    // An unblocked automation turn and an unblocked person turn each run this many tool rounds and then reply.
    private const int Rounds = 3;
    private const string FireKeyValue = "reminder-a:1792764000000";
    private const string JobId = "preempt-job-1";
    private const string JobKey = "bg-job:" + JobId;
    private const string StartJob = "Start the export job.";
    private const string ActiveJobsBlock = "[active-background-jobs]";

    // The fixed stop note (openspec/changes/automation-stop-followups, "A person ends an automation turn"). It holds no
    // kind and no sender text, because a webhook sets its own source kind. The eval automation_stop_note_person_first
    // reads the same literal from the source.
    private const string StopNote = "The automated turn above stopped unfinished because a person sent a message. "
                                    + "Answer the person's message. Do not continue the stopped work unless the person asks for it.";

    // Above the compaction limit of the 128k context window (0.75 * 128k).
    private static readonly UsageDetails OverLimit = new() { InputTokenCount = 200_000, OutputTokenCount = 1, TotalTokenCount = 200_001 };

    private static readonly Regex LoopPattern = new(@"loop:([a-z0-9]+)", RegexOptions.Compiled);

    private readonly TurnProbeTool _probe = new();

    public enum Row
    {
        /// <summary>The incident: a reminder turn runs a tool loop, and the operator sends a message.</summary>
        ReminderTurnThenPerson,

        /// <summary>
        /// An approval notice turn is automation (child-approval-events-and-parent-pause: "The notice turn is
        /// automation"). No design says that a notice turn must complete. The model read the notice in the first call,
        /// and a notice turn that starts gives its answers (design D1.3), so the person may end it.
        /// </summary>
        NoticeTurnThenPerson,

        /// <summary>
        /// A background job result turn is automation. Alice's earlier turn started the job. The ended result turn must
        /// still close the job, so the person's prompt does not list it under the active background jobs. The same stale
        /// entry makes a later passivation report the job as killed. The job also stays closed after the session
        /// recovers from its journal.
        /// </summary>
        BackgroundJobTurnThenPerson,

        /// <summary>Unchanged: Bob's input waits until Alice's turn ends. A person never ends the turn of a person.</summary>
        PersonTurnThenOtherPerson,

        /// <summary>Unchanged: a second reminder fire has the same requester, so it joins the running reminder turn.</summary>
        ReminderTurnThenReminder,

        /// <summary>Unchanged: Alice's second input joins her running turn (#2607).</summary>
        PersonTurnThenSamePerson,

        /// <summary>
        /// #2633: the reminder turn reaches the compaction limit at the tool batch where the operator's message waits.
        /// The compaction commits after the close and before the operator's first model call.
        /// </summary>
        DueCompactionAtTheEndPoint,

        /// <summary>
        /// #2633: the reminder turn reaches the compaction limit at its tool batch with no person waiting, so a
        /// tool-loop compaction runs. The operator writes while the compaction summary call is held. At the compaction
        /// end the reminder turn ends: it makes no model call after the compaction.
        /// </summary>
        PersonDuringToolLoopCompaction,

        /// <summary>
        /// #2677 review: a second reminder fire (same requester) waits at the tool batch of the first, where a
        /// compaction is due, so it joins the turn and the tool-loop compaction starts. The operator writes during the
        /// compaction. The stop must not close the second fire without a model call: every input reaches the model.
        /// </summary>
        ReminderJoinsBeforeToolLoopCompactionThenPerson,

        /// <summary>
        /// #2677 review: Alice's child ends while the reminder turn runs, so its result notice (Alice's requester) waits
        /// before Alice's message, and both start one turn. That turn is Alice's turn, so it takes the stop note. A later
        /// turn of Alice holds no second note.
        /// </summary>
        PersonWithOwnChildNoticeTakesTheNote,

        /// <summary>
        /// #2677 review: a second reminder fire waits before Alice's message, so after the stop it runs first in FIFO
        /// order, and Alice ends it too. Its requests hold no note. Alice's turn holds the note once.
        /// </summary>
        AutomationRunsBeforePersonAfterStop
    }

    private enum Effect { EndsTurn, Waits, Joins }

    private enum Compaction { None, AtTheEndPoint, DuringToolLoop }

    private sealed record Case(string Running, MessageSource? RunningSource, string Buffered, MessageSource BufferedSource,
        Effect Effect, string? FireKey, bool StartsJob = false, Compaction Compaction = Compaction.None,
        string? Extra = null, MessageSource? ExtraSource = null, bool ChildNoticeBeforePerson = false, string? Later = null);

    // The second reminder fire of the #2677 rows. It has the same requester as the first fire.
    private const string SecondFireKey = "reminder-b:1792764060000";

    protected override void RegisterParentTools(ToolRegistry registry)
    {
        registry.RegisterCore(_probe);
        registry.RegisterCore(AIFunctionFactory.Create((string command) => $"ran {command}", ShellTool.ToolName), "shell");
    }

    // The fake job manager below needs a background launch. The real launch authorizes a real shell command, so this
    // wrapper gives a fixed launch and sends every other call to the real executor.
    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        var inner = (IToolExecutor)services.Last(descriptor => descriptor.ServiceType == typeof(IToolExecutor)).ImplementationInstance!;
        services.AddSingleton<IToolExecutor>(new JobLaunchExecutor(inner, TestPaths.WorkspacesDirectory));
        // The base settings, and KeepRecentMessages = 0: a short history still reaches the compaction summary call, so a
        // row can hold a compaction open. No other row reaches the compaction limit.
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning { SnapshotInterval = SnapshotInterval, TitleGenerationInterval = 0, KeepRecentMessages = 0 }
        });
    }

    [Theory]
    [InlineData(Row.ReminderTurnThenPerson)]
    [InlineData(Row.NoticeTurnThenPerson)]
    [InlineData(Row.BackgroundJobTurnThenPerson)]
    [InlineData(Row.PersonTurnThenOtherPerson)]
    [InlineData(Row.ReminderTurnThenReminder)]
    [InlineData(Row.PersonTurnThenSamePerson)]
    [InlineData(Row.DueCompactionAtTheEndPoint)]
    [InlineData(Row.PersonDuringToolLoopCompaction)]
    [InlineData(Row.ReminderJoinsBeforeToolLoopCompactionThenPerson)]
    [InlineData(Row.PersonWithOwnChildNoticeTakesTheNote)]
    [InlineData(Row.AutomationRunsBeforePersonAfterStop)]
    public async Task A_person_input_ends_a_running_automation_turn_at_the_next_safe_boundary(Row row)
    {
        // Arrange
        var sessionId = new SessionId($"person-ends-automation/{row}");
        var journal = await JournalAsync();
        Clients.Parent.ResponseContentsSelector = LoopReply;
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var spec = CaseOf(row);
        var hold = NewGate();
        var childGate = NewGate();
        var summaryGate = NewGate();
        var bufferedHold = NewGate();
        var entered = ParentGate.Arm(request => OwnerLabel(request) == spec.Running && RoundsOf(request, spec.Running) == 0, hold);
        if (spec.Compaction != Compaction.None)
        {
            // The summary keeps the task text of the running turn, as a real summary quotes the task. A model call
            // after the compaction then still counts as a call of the running turn.
            Clients.B.ObservationResponseOverride = Text(spec.Running);
        }

        try
        {
            // Act: the first model call of the running turn holds. The second input waits in the buffer. The model then
            // asks for one tool, so the running turn reaches a tool-batch end with the input in the buffer.
            if (spec.StartsJob)
            {
                ActorRegistry.For(Sys).Register<BackgroundJobManagerActorKey>(FakeJobManager().Ref, overwrite: true);
                await SendAsync(sessionId, StartJob, Operator(Alice));
                await ExpectTurnsAsync(subscriber, 1);
            }

            ChildRunAccepted? personChild = null;
            if (spec.ChildNoticeBeforePerson)
                (personChild, childGate) = await StartConsentChildAsync(sessionId, subscriber, AliceSource, "plain");

            if (spec.RunningSource is { } runningSource)
                await SendAsync(sessionId, Text(spec.Running), runningSource);
            else
            {
                (_, childGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-preempt");
                childGate.TrySetResult();
            }

            await entered.WaitAsync(Deadline, Cancellation);
            // The first gate is empty again. This gate holds the first model request that carries the second input.
            var bufferedEntered = ParentGate.Arm(
                request => OwnerLabel(request) == spec.Buffered && RoundsOf(request, spec.Buffered) == 0, bufferedHold);
            if (spec.Compaction != Compaction.None)
                Clients.Parent.PlannedUsageOverrides.Enqueue(OverLimit);
            // An input that waits before the second input: an automation input, or the result notice of Alice's child.
            if (spec.Extra is { } extra)
                await SendAsync(sessionId, Text(extra), spec.ExtraSource!);
            if (personChild is not null)
            {
                childGate.TrySetResult();
                await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, personChild.Run.RunId)), Deadline,
                    cancellationToken: Cancellation);
            }

            if (spec.Compaction == Compaction.DuringToolLoop)
            {
                // No input waits at the tool batch, so a tool-loop compaction starts. Its summary call holds.
                Clients.B.ObservationResponseGate = summaryGate;
                hold.TrySetResult();
                await AwaitAssertAsync(async () =>
                {
                    Assert.True(Clients.B.CallCount > 0, "The compaction summary call did not start.");
                    Assert.Equal("Compacting", await PhaseOfAsync(sessionId));
                }, Deadline, cancellationToken: Cancellation);
                await SendAsync(sessionId, Text(spec.Buffered), spec.BufferedSource);
                summaryGate.TrySetResult();
            }
            else
            {
                await SendAsync(sessionId, Text(spec.Buffered), spec.BufferedSource);
                hold.TrySetResult();
            }

            // Assert
            await bufferedEntered.WaitAsync(Deadline, Cancellation);
            AssertCompactionsBeforeTheSecondInput(journal, sessionId, spec);
            bufferedHold.TrySetResult();
            if (spec.Later is { } later)
            {
                // A later turn of the same person, after the session is idle again.
                await AwaitSettledAsync(journal, sessionId, null);
                await SendAsync(sessionId, Text(later), spec.BufferedSource);
            }

            await AssertOutcomeAsync(journal, sessionId, subscriber, spec);
            if (spec.StartsJob)
                await AssertJobStaysClosedAfterRecoveryAsync(journal, sessionId);
        }
        finally
        {
            hold.TrySetResult();
            childGate.TrySetResult();
            summaryGate.TrySetResult();
            bufferedHold.TrySetResult();
            ParentGate.Disarm();
        }
    }

    // The precondition at the first model request that carries the second input: a compaction row committed exactly one
    // compaction before it, and no other row committed one. A compaction after that request comes too late, because
    // that request is the call that can overflow.
    private static void AssertCompactionsBeforeTheSecondInput(AcceptanceFault journal, SessionId sessionId, Case spec)
    {
        var compactions = journal.Committed.ToArray().OfType<SessionCompacted>().Count(evt => evt.SessionId == sessionId);
        Assert.True(compactions == (spec.Compaction == Compaction.None ? 0 : 1),
            $"{compactions} compaction(s) committed before the first model request of the second input. The row expects "
            + $"{(spec.Compaction == Compaction.None ? 0 : 1)}.");
    }

    private static Case CaseOf(Row row) => row switch
    {
        Row.ReminderTurnThenPerson => new("reminder", Reminder(FireKeyValue), "alice", AliceSource, Effect.EndsTurn, FireKeyValue),
        Row.NoticeTurnThenPerson => new("notice", null, "alice", AliceSource, Effect.EndsTurn, null),
        Row.BackgroundJobTurnThenPerson => new("job", JobResult(), "alice", AliceSource, Effect.EndsTurn, null, StartsJob: true),
        Row.PersonTurnThenOtherPerson => new("alice", AliceSource, "bob", OperatorWithTargets(Bob, BobTarget), Effect.Waits, null),
        Row.ReminderTurnThenReminder => new("reminder", Reminder(FireKeyValue), "reminder2", Reminder("reminder-b:1792764060000"),
            Effect.Joins, FireKeyValue),
        Row.PersonTurnThenSamePerson => new("alice", AliceSource, "alice2", AliceSource, Effect.Joins, null),
        Row.DueCompactionAtTheEndPoint => new("reminder", Reminder(FireKeyValue), "alice", AliceSource, Effect.EndsTurn, FireKeyValue,
            Compaction: Compaction.AtTheEndPoint),
        Row.PersonDuringToolLoopCompaction => new("reminder", Reminder(FireKeyValue), "alice", AliceSource, Effect.EndsTurn, FireKeyValue,
            Compaction: Compaction.DuringToolLoop),
        Row.ReminderJoinsBeforeToolLoopCompactionThenPerson => new("reminder", Reminder(FireKeyValue), "alice", AliceSource,
            Effect.EndsTurn, FireKeyValue, Compaction: Compaction.DuringToolLoop, Extra: "reminder2", ExtraSource: Reminder(SecondFireKey)),
        Row.PersonWithOwnChildNoticeTakesTheNote => new("reminder", Reminder(FireKeyValue), "alice", AliceSource, Effect.EndsTurn,
            FireKeyValue, ChildNoticeBeforePerson: true, Later: "alice2"),
        Row.AutomationRunsBeforePersonAfterStop => new("reminder", Reminder(FireKeyValue), "alice", AliceSource, Effect.EndsTurn,
            FireKeyValue, Extra: "reminder2", ExtraSource: Reminder(SecondFireKey)),
        _ => throw new ArgumentOutOfRangeException(nameof(row))
    };

    private static MessageSource AliceSource => OperatorWithTargets(Alice, AliceTarget);

    // The source shape of BackgroundJobManagerActor.DeliverResultToSession.
    private static MessageSource JobResult() => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId("background-job-system"),
        MessageId = JobKey, TurnId = new TurnId(JobKey), BackgroundJobId = new BackgroundJobId(JobKey),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind(BackgroundJobManagerActor.SourceKind)
        },
        ReceivedAt = TimeProvider.System.GetUtcNow()
    };

    private TestProbe FakeJobManager()
    {
        var manager = CreateTestProbe("job-manager");
        manager.SetAutoPilot(new DelegateAutoPilot((sender, message) =>
        {
            if (message is StartBackgroundJob)
                sender.Tell(new BackgroundJobStarted(new BackgroundJobId(JobId), null));
            else if (message is KillJobsForSession kill)
                sender.Tell(new SessionJobsReaped(kill.SessionId, 0));
            return AutoPilot.KeepRunning;
        }));
        return manager;
    }

    // The thread of the delivery target that each label's requester carries. A tool context of a reminder or a notice
    // turn has no target here: the reminder source has none, and the notice copies the targets of Operator(Alice).
    private static string AuthorityOf(string label) => label switch
    {
        "alice" or "alice2" => "alice-thread",
        "bob" => "bob-thread",
        _ => "none"
    };

    // The shared pre- and postconditions of each row:
    // - every admitted input of the session is consumed exactly once, and the session is idle (the buffer is empty);
    // - every tool call runs under the authority of the requester whose message asked for it, so no model call mixes
    //   two authorities;
    // - the running turn ends once: one TurnCompleted, and for a reminder the one TurnCompleted that names its fire;
    // - every tool result in each request has its tool call in the same request, also after a compaction;
    // - EndsTurn: the running turn made one model call and one tool call. Then a new turn for the person starts. Its
    //   first request has the history of the ended turn (its tool result, or the compaction summary), then the stop
    //   note, and then the person's message. Each request of the person's turn holds the note exactly once. No other
    //   request holds a note of its own: a turn that runs before the person's turn holds none, and a later turn keeps
    //   only the person's note in its history, before the person's first message;
    // - every labeled input reaches at least one model request, so no stop closes an input that the model never saw;
    // - Waits and Joins: no request holds the stop note;
    // - Joins: the same request shape, but in the running turn (one TurnCompleted);
    // - Waits: the running turn runs all its rounds before any request carries the waiting input;
    // - no request of the second input's turn lists an active background job. In the job row, the result turn lists
    //   the job, so the check is not empty.
    private async Task AssertOutcomeAsync(AcceptanceFault journal, SessionId sessionId, Akka.TestKit.TestProbe subscriber, Case spec)
    {
        await AwaitInputAdmittedAsync(journal, Text(spec.Buffered));
        var turns = new List<TurnCompleted>();
        await AwaitSettledAsync(journal, sessionId, () => CollectTurns(subscriber, turns));
        await BarrierAsync(sessionId);
        CollectTurns(subscriber, turns);

        var requests = Clients.Parent.ReceivedMessages;
        var runningRequests = requests.Count(request => OwnerLabel(request) == spec.Running);
        var lastRunning = LastIndexOf(requests, request => OwnerLabel(request) == spec.Running);
        var firstBuffered = IndexOf(requests, request => OwnerLabel(request) == spec.Buffered);
        var calls = _probe.Calls.ToArray();
        Note($"effect={spec.Effect} runningRequests={runningRequests} lastRunning={lastRunning} firstBuffered={firstBuffered} "
             + $"calls=[{string.Join(",", calls.Select(call => $"{call.Label}@{call.Authority}"))}] "
             + $"turns=[{string.Join(",", turns.Select(turn => $"{turn.Outcome}/quiet={turn.QuietEnd}/{turn.SourceReminderId?.Value}"))}]");

        // No stop closes an input that never reached the model.
        Assert.All(Inputs(journal, sessionId).Select(static input => input.UserMessage.Content)
                .Where(static content => content.StartsWith("loop:", StringComparison.Ordinal)),
            content => Assert.True(requests.Any(request => request.Any(message => message.Text.Contains(content, StringComparison.Ordinal))),
                $"The input \"{content}\" was consumed, but no model request carried it."));

        // No input is lost or consumed twice.
        Assert.All(Inputs(journal, sessionId), input => Assert.Equal(1, ConsumptionCount(journal, input.InputId)));

        // No compaction splits a tool call from its result.
        Assert.All(requests, request => Assert.Empty(Results(request).Select(static result => result.CallId)
            .Except(Calls(request).Select(static call => call.CallId))));

        // One authority for each call.
        Assert.All(calls, call => Assert.Equal(AuthorityOf(call.Label), call.Authority));
        Assert.Equal(Rounds, calls.Count(call => call.Label == spec.Buffered));

        // The running turn.
        Assert.True(firstBuffered >= 0, "No model request carried the second input.");
        if (spec.Effect == Effect.Waits)
        {
            Assert.Equal(Rounds + 1, runningRequests);
            Assert.True(firstBuffered > lastRunning, "The waiting input reached the model before the running turn ended.");
        }
        else
        {
            Assert.True(runningRequests == 1,
                $"The running turn made {runningRequests} model calls. It must make no model call after the safe boundary "
                + "(a tool-batch end or a tool-loop compaction end) at which the second input waited.");
            Assert.Equal(1, calls.Count(call => call.Label == spec.Running));
            var tail = FreshTail(requests[firstBuffered]);
            var shapeApplies = spec.Extra is null && !spec.ChildNoticeBeforePerson;
            // A compaction with KeepRecentMessages = 0 replaces the tool call and its result with the summary.
            // A turn or a notice between the ended turn and the person changes the tail, so those rows skip the shape.
            if (shapeApplies)
                Assert.Equal(spec.Compaction == Compaction.None ? [$"loop-{spec.Running}-1"] : Array.Empty<string>(),
                    Results(tail).Select(result => result.CallId));
            // Each request can hold a per-turn system context message ("[system: ..."), and after a compaction the
            // summary ("[session-summary ..."). Neither is a new input.
            var userTexts = tail.Where(message => message.Role == AiChatRole.User
                                                  && !message.Text.StartsWith("[system: ", StringComparison.Ordinal)
                                                  && !message.Text.StartsWith("[session-summary", StringComparison.Ordinal))
                .Select(message => message.Text).ToArray();
            Assert.True(!shapeApplies || userTexts.Length == 1 && userTexts[0].Contains(Text(spec.Buffered), StringComparison.Ordinal),
                $"The request must add only the second input after the tool result. It added [{string.Join(" | ", userTexts)}].");
        }

        // The stop note: in each request of the person's turn when the running turn ended, and nowhere else.
        if (spec.Effect == Effect.EndsTurn)
            Assert.All(requests, request => AssertStopNote(request, spec, required: OwnerLabel(request) == spec.Buffered));
        else
            Assert.DoesNotContain(requests, request => request.Any(message => message.Text.Contains(StopNote, StringComparison.Ordinal)));

        // A turn that ends early still closes the background job that it reports.
        if (spec.StartsJob)
            Assert.Contains(requests, request => OwnerLabel(request) == spec.Running && ListsActiveJob(request));
        Assert.False(requests.Any(request => OwnerLabel(request) == spec.Buffered && ListsActiveJob(request)),
            "A request of the second input's turn lists a background job as active after the turn that reported it ended.");

        // The running turn ends once.
        // An extra input and a later input each run one more turn.
        Assert.Equal((spec.Effect == Effect.Joins ? 1 : 2) + (spec.Extra is null ? 0 : 1) + (spec.Later is null ? 0 : 1), turns.Count);
        Assert.All(turns, turn => Assert.Equal(TurnOutcome.Completed, turn.Outcome));
        Assert.Equal(spec.Effect == Effect.EndsTurn, turns[0].QuietEnd);
        if (spec.FireKey is { } fireKey)
            Assert.Same(turns[0], Assert.Single(turns, turn => turn.SourceReminderId?.Value == fireKey));
    }

    // The note sits in one per-turn system context message. It follows the history of the ended turn and precedes the
    // person's message, so the person's message stays the last real user input. A later call of the turn keeps that
    // history entry and adds no second note.
    private static void AssertStopNote(IReadOnlyList<ChatMessage> request, Case spec, bool required)
    {
        var notes = Enumerable.Range(0, request.Count)
            .Where(index => request[index].Text.Contains(StopNote, StringComparison.Ordinal)).ToArray();
        Assert.True(notes.Length == (required ? 1 : notes.Length) && notes.Length <= 1,
            $"The request of \"{OwnerLabel(request)}\" holds the stop note {notes.Length} time(s). "
            + (required ? "It must hold it once." : "It may hold at most the person's note."));
        if (notes.Length == 0)
            return;
        var note = request[notes[0]];
        Assert.True(note.Role == AiChatRole.User && note.Text.StartsWith("[system:", StringComparison.Ordinal),
            $"The stop note is not in a system context message: {note.Role} \"{note.Text}\".");
        // The person's first message. A later message of the person comes after it.
        var person = Enumerable.Range(0, request.Count).Where(index => request[index].Role == AiChatRole.User
                && request[index].Text.Contains(Text(spec.Buffered), StringComparison.Ordinal)).DefaultIfEmpty(-1).First();
        var endedHistory = LastIndexOf(request, message => message.Contents.OfType<FunctionResultContent>()
                                                               .Any(result => result.CallId.StartsWith($"loop-{spec.Running}-", StringComparison.Ordinal))
                                                           || message.Text.StartsWith("[session-summary", StringComparison.Ordinal));
        Assert.True(endedHistory >= 0 && endedHistory < notes[0] && notes[0] < person,
            $"The stop note is at {notes[0]}. It must follow the ended turn's history ({endedHistory}) and precede the "
            + $"person's message ({person}).");
    }

    // Every admitted input is consumed, and the session is idle. Each barrier gives the caller a moment to read the
    // outputs that the session sent before it.
    private async Task AwaitSettledAsync(AcceptanceFault journal, SessionId sessionId, Action? afterBarrier)
    {
        await AwaitAssertAsync(async () =>
        {
            await BarrierAsync(sessionId);
            afterBarrier?.Invoke();
            Assert.All(Inputs(journal, sessionId), input => Assert.True(ConsumedAt(journal, input.InputId) >= 0,
                $"The input \"{input.UserMessage.Content}\" is still pending."));
            Assert.True(await IsIdleAsync(sessionId), "The session still runs a turn.");
        }, Deadline, cancellationToken: Cancellation);
    }

    // The ended job turn wrote an InputClosed with no input ID and the job ID. The session recovers from its journal
    // only (no snapshot). The job stays closed: a repeat of its result starts no turn, and the next prompt does not list
    // the job as active. Each earlier input, also the person's input, stays consumed exactly once.
    private async Task AssertJobStaysClosedAfterRecoveryAsync(AcceptanceFault journal, SessionId sessionId)
    {
        await RestartSessionAsync(sessionId);
        var admitted = Inputs(journal, sessionId).Count;
        await SendAsync(sessionId, Text("job"), JobResult());
        await SendAsync(sessionId, Text("alice3"), AliceSource);
        await AwaitAssertAsync(async () =>
        {
            await BarrierAsync(sessionId);
            Assert.All(Inputs(journal, sessionId), input => Assert.True(ConsumedAt(journal, input.InputId) >= 0,
                $"The input \"{input.UserMessage.Content}\" is still pending after recovery."));
            Assert.True(await IsIdleAsync(sessionId), "The session still runs a turn.");
        }, Deadline, cancellationToken: Cancellation);

        Assert.Equal(admitted + 1, Inputs(journal, sessionId).Count);
        Assert.All(Inputs(journal, sessionId), input => Assert.Equal(1, ConsumptionCount(journal, input.InputId)));
        var next = Clients.Parent.ReceivedMessages.First(request => OwnerLabel(request) == "alice3");
        Assert.False(ListsActiveJob(next), "After recovery, the prompt lists the closed background job as active.");
    }

    // The per-turn context hint ("[system: ...") stays in history, so only the fresh tail shows the current state.
    private static bool ListsActiveJob(IReadOnlyList<ChatMessage> request)
        => FreshTail(request).Any(message => message.Text.Contains(ActiveJobsBlock, StringComparison.Ordinal));

    private static IReadOnlyList<InputAdmitted> Inputs(AcceptanceFault journal, SessionId sessionId)
        => journal.Committed.OfType<InputAdmitted>().Where(admitted => admitted.SessionId == sessionId).ToArray();

    // The session tells each output to the subscriber before it answers the barrier, so the outputs are in the queue.
    private static void CollectTurns(Akka.TestKit.TestProbe subscriber, List<TurnCompleted> turns)
    {
        while (subscriber.HasMessages)
        {
            if (subscriber.ReceiveOne(TimeSpan.Zero) is TurnCompleted turn)
                turns.Add(turn);
        }
    }

    private static int LastIndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (match(items[index]))
                return index;
        }

        return -1;
    }

    private static string Text(string label) => $"loop:{label}";

    // The model works for the requester of the newest labeled user message: a "loop:{label}" message, or an approval
    // notice. It calls turn_probe once for each round, and after the last round it replies with text. A request with no
    // labeled message gets the base script, for instance the spawn turn of the notice row.
    private static IReadOnlyList<AIContent>? LoopReply(IReadOnlyList<ChatMessage> request)
    {
        if (FreshUserTexts(request).Contains(StartJob))
        {
            return [new FunctionCallContent("start-job", ShellTool.ToolName, new Dictionary<string, object?>
            {
                ["command"] = "sleep 600", ["_background"] = true, ["_rationale"] = "Start the export job."
            })];
        }

        if (OwnerLabel(request) is not { } label)
            return ScriptedParentReply(request);
        var round = RoundsOf(request, label);
        return round < Rounds ? [TurnProbeTool.Call(label, round + 1)] : null;
    }

    private static string? OwnerLabel(IReadOnlyList<ChatMessage> request)
    {
        for (var index = request.Count - 1; index >= 0; index--)
        {
            if (request[index].Role != AiChatRole.User
                || request[index].Text.Contains("[working-context]", StringComparison.Ordinal))
                continue;
            var text = request[index].Text;
            if (text.Contains(OpenNoticeHeading, StringComparison.Ordinal))
                return "notice";
            if (LoopPattern.Match(text) is { Success: true } match)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static int RoundsOf(IReadOnlyList<ChatMessage> request, string label)
        => Results(request).Count(result => result.CallId.StartsWith($"loop-{label}-", StringComparison.Ordinal));

    /// <summary>
    /// Records the delivery thread of the tool context of each call. The context comes from the authority of the turn
    /// that runs the call, so the thread names the requester whose authority the call used.
    /// </summary>
    private sealed class TurnProbeTool : INetclawTool
    {
        private const string ToolName = "turn_probe";

        public ConcurrentQueue<(string Label, string Authority)> Calls { get; } = new();
        public string Name => ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Record the authority of the turn.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "recorded", Name, Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            var label = arguments?["label"]?.ToString() ?? throw new InvalidOperationException("The label is required.");
            Calls.Enqueue((label, context.EffectiveDeliveryTarget?.ThreadOrRootId ?? "none"));
            return Task.FromResult("recorded");
        }

        // Each round has new arguments, so the repeat guard of the session does not refuse the call.
        public static FunctionCallContent Call(string label, int round) => new($"loop-{label}-{round}", ToolName,
            new Dictionary<string, object?> { ["label"] = label, ["round"] = round, ["_rationale"] = "Record the turn authority." });
    }

    private sealed class JobLaunchExecutor(IToolExecutor inner, string workingDirectory) : IToolExecutor
    {
        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
            => inner.ExecuteAsync(toolCall, context, ct);

        public IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
            => inner.ExecuteStreamAsync(toolCall, context, ct);

        public ToolLivenessMode GetLivenessMode(FunctionCallContent toolCall) => inner.GetLivenessMode(toolCall);

        public ToolArgumentRejection? ValidateToolCall(FunctionCallContent toolCall) => inner.ValidateToolCall(toolCall);

        public ToolCallInterpretation InterpretToolCall(FunctionCallContent toolCall) => inner.InterpretToolCall(toolCall);

        public (ToolCallMeta? Meta, FunctionCallContent Cleaned) PrepareToolCall(FunctionCallContent toolCall)
            => inner.PrepareToolCall(toolCall);

        public Task<ShellProcessLaunch> PrepareShellLaunchAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(new ShellProcessLaunch(
                ToolArgumentHelper.GetString(toolCall.Arguments, "command")!,
                workingDirectory,
                context.Invocation,
                new Netclaw.Security.ShellCommandPolicy(TestShellEnvironment.Current),
                new Netclaw.Security.ToolPathPolicy(TestShellEnvironment.Current, []),
                static _ => Task.CompletedTask));
    }
}
