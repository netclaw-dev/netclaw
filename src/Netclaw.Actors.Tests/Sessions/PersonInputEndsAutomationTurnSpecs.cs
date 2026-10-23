// -----------------------------------------------------------------------
// <copyright file="PersonInputEndsAutomationTurnSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// A person's input ends a running automation turn at the next tool-batch end.
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
/// posts no empty-turn fallback, and a reminder fire records success. History gets no extra marker: the next model
/// request holds the tool result of the ended turn and then the person's message, the same shape as a mid-turn join.
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

        /// <summary>Unchanged: Bob's input waits until Alice's turn ends. A person never ends the turn of a person.</summary>
        PersonTurnThenOtherPerson,

        /// <summary>Unchanged: a second reminder fire has the same requester, so it joins the running reminder turn.</summary>
        ReminderTurnThenReminder,

        /// <summary>Unchanged: Alice's second input joins her running turn (#2607).</summary>
        PersonTurnThenSamePerson
    }

    private enum Effect { EndsTurn, Waits, Joins }

    private sealed record Case(string Running, MessageSource? RunningSource, string Buffered, MessageSource BufferedSource,
        Effect Effect, string? FireKey);

    protected override void RegisterParentTools(ToolRegistry registry) => registry.RegisterCore(_probe);

    [Theory]
    [InlineData(Row.ReminderTurnThenPerson)]
    [InlineData(Row.NoticeTurnThenPerson)]
    [InlineData(Row.PersonTurnThenOtherPerson)]
    [InlineData(Row.ReminderTurnThenReminder)]
    [InlineData(Row.PersonTurnThenSamePerson)]
    public async Task A_person_input_ends_a_running_automation_turn_at_the_next_tool_batch(Row row)
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
        var entered = ParentGate.Arm(request => OwnerLabel(request) == spec.Running && RoundsOf(request, spec.Running) == 0, hold);

        try
        {
            // Act: the first model call of the running turn holds. The second input waits in the buffer. The model then
            // asks for one tool, so the running turn reaches a tool-batch end with the input in the buffer.
            if (spec.RunningSource is { } runningSource)
                await SendAsync(sessionId, Text(spec.Running), runningSource);
            else
            {
                (_, childGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-preempt");
                childGate.TrySetResult();
            }

            await entered.WaitAsync(Deadline, Cancellation);
            await SendAsync(sessionId, Text(spec.Buffered), spec.BufferedSource);
            hold.TrySetResult();

            // Assert
            await AssertOutcomeAsync(journal, sessionId, subscriber, spec);
        }
        finally
        {
            hold.TrySetResult();
            childGate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    private static Case CaseOf(Row row) => row switch
    {
        Row.ReminderTurnThenPerson => new("reminder", Reminder(FireKeyValue), "alice", AliceSource, Effect.EndsTurn, FireKeyValue),
        Row.NoticeTurnThenPerson => new("notice", null, "alice", AliceSource, Effect.EndsTurn, null),
        Row.PersonTurnThenOtherPerson => new("alice", AliceSource, "bob", OperatorWithTargets(Bob, BobTarget), Effect.Waits, null),
        Row.ReminderTurnThenReminder => new("reminder", Reminder(FireKeyValue), "reminder2", Reminder("reminder-b:1792764060000"),
            Effect.Joins, FireKeyValue),
        Row.PersonTurnThenSamePerson => new("alice", AliceSource, "alice2", AliceSource, Effect.Joins, null),
        _ => throw new ArgumentOutOfRangeException(nameof(row))
    };

    private static MessageSource AliceSource => OperatorWithTargets(Alice, AliceTarget);

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
    // - EndsTurn: the running turn made one model call and one tool call. Then a new turn for the person starts. Its
    //   first request has the tool result of the ended turn and then the person's message, with nothing between them;
    // - Joins: the same request shape, but in the running turn (one TurnCompleted);
    // - Waits: the running turn runs all its rounds before any request carries the waiting input.
    private async Task AssertOutcomeAsync(AcceptanceFault journal, SessionId sessionId, Akka.TestKit.TestProbe subscriber, Case spec)
    {
        await AwaitInputAdmittedAsync(journal, Text(spec.Buffered));
        var turns = new List<TurnCompleted>();
        await AwaitAssertAsync(async () =>
        {
            await BarrierAsync(sessionId);
            CollectTurns(subscriber, turns);
            Assert.All(Inputs(journal, sessionId), input => Assert.True(ConsumedAt(journal, input.InputId) >= 0,
                $"The input \"{input.UserMessage.Content}\" is still pending."));
            Assert.True(await IsIdleAsync(sessionId), "The session still runs a turn.");
        }, Deadline, cancellationToken: Cancellation);
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

        // No input is lost or consumed twice.
        Assert.All(Inputs(journal, sessionId), input => Assert.Equal(1, ConsumptionCount(journal, input.InputId)));

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
                $"The running turn made {runningRequests} model calls. It must make no model call after the tool batch "
                + "that ended with the second input in the buffer.");
            Assert.Equal(1, calls.Count(call => call.Label == spec.Running));
            var tail = FreshTail(requests[firstBuffered]);
            Assert.Equal([$"loop-{spec.Running}-1"], Results(tail).Select(result => result.CallId));
            // Each request can end with a transient runtime hint ("[system: ..."). It is not history.
            var userTexts = tail.Where(message => message.Role == AiChatRole.User
                                                  && !message.Text.StartsWith("[system: ", StringComparison.Ordinal))
                .Select(message => message.Text).ToArray();
            Assert.True(userTexts.Length == 1 && userTexts[0].Contains(Text(spec.Buffered), StringComparison.Ordinal),
                $"The request must add only the second input after the tool result. It added [{string.Join(" | ", userTexts)}].");
        }

        // The running turn ends once.
        Assert.Equal(spec.Effect == Effect.Joins ? 1 : 2, turns.Count);
        Assert.All(turns, turn => Assert.Equal(TurnOutcome.Completed, turn.Outcome));
        Assert.Equal(spec.Effect == Effect.EndsTurn, turns[0].QuietEnd);
        if (spec.FireKey is { } fireKey)
            Assert.Same(turns[0], Assert.Single(turns, turn => turn.SourceReminderId?.Value == fireKey));
    }

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

    private static int LastIndexOf(IReadOnlyList<IReadOnlyList<ChatMessage>> requests, Func<IReadOnlyList<ChatMessage>, bool> match)
    {
        for (var index = requests.Count - 1; index >= 0; index--)
        {
            if (match(requests[index]))
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
}
