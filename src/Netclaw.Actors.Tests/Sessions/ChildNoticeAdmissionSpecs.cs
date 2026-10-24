// -----------------------------------------------------------------------
// <copyright file="ChildNoticeAdmissionSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for how a notice joins, waits, and orders among other inputs.
/// Rule 1: a turn has one authority, fixed when it starts. A notice joins a running turn only for the same requester.
/// Rule 2: every source delivers work as an admitted input, and inputs run in admission order.
/// </summary>
public sealed class ChildNoticeAdmissionSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// Specs 4 and 5 (S2 decisions a and b). A turn keeps its authority from its first tool call to its last, when a
    /// child result becomes ready during the turn. Alice's notice joins Alice's turn and leaves its authority alone.
    /// Alice's notice waits for its own turn when Bob's turn runs, and Bob's later tool calls keep Bob's authority.
    /// Source: tool-authorization TA-1, netclaw-session "Tool execution encapsulation", and the plan rules 1 and 2.
    /// Planted bug: a notice joins any running turn, or the joined notice replaces the turn authority with the context
    /// of the child.
    /// </summary>
    [Theory]
    [InlineData(Alice, true)]
    [InlineData(Bob, false)]
    public async Task Turn_authority_stays_fixed_across_tool_batches_when_a_child_notice_is_admitted(
        string secondRequester, bool spawnTurnAdopted)
    {
        // Arrange
        var sessionId = new SessionId($"notice/authority-{secondRequester}");
        var journal = await JournalAsync();
        Clients.Parent.ResponseContentsSelector = TwoProbesAndNotices;
        var subscriber = await JoinAsync(sessionId);
        var (accepted, childGate) = await StartChildAsync(sessionId, Alice, "one", adopted: spawnTurnAdopted);
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            // Act: the child result becomes ready while the first approval of the second turn waits for an answer.
            await SendAsync(sessionId, "probes:two", Operator(secondRequester));
            var first = await FishApprovalAsync(subscriber, "second-turn-probe-1");
            Note($"first approval requester={first.RequesterSenderId?.Value}");
            childGate.TrySetResult();
            await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, accepted.Run.RunId),
                "The child result must become ready while the first approval waits."), Deadline, cancellationToken: Cancellation);
            AssertAck(await AnswerAsync(sessionId, first.CallId, secondRequester));
            var second = await FishApprovalAsync(subscriber, "second-turn-probe-2");
            AssertAck(await AnswerAsync(sessionId, second.CallId, secondRequester));
            await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ToolCallRecorded>(),
                evt => evt.ToolResult.ToolCallId?.Value == "second-turn-probe-2"), Deadline, cancellationToken: Cancellation);

            // Assert: both approvals of the second turn carry the authority of the second turn.
            var secondTurn = journal.Committed.OfType<InputAdmitted>().Single(evt => evt.UserMessage.Content == "probes:two").TurnContext;
            var observed = journal.Committed.OfType<ToolApprovalRequested>()
                .Where(evt => evt.CallId.StartsWith("second-turn-probe", StringComparison.Ordinal))
                .Select(evt => Observe(evt.CallId, evt.TurnContext))
                .ToArray();
            Note($"second turn admitted: {Observe("admitted", secondTurn)}");
            Note($"spawn turn (child original): {Observe("child", accepted.Run.OriginalContext)}");
            foreach (var item in observed)
                Note($"authorization attempt: {item}");
            Assert.Equal(
                [Observe("second-turn-probe-1", secondTurn), Observe("second-turn-probe-2", secondTurn)],
                observed);

            // The notice joins the turn of the same requester and waits for its own turn when the requester differs.
            await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count == 1,
                "Alice's child must yield one notice."), Deadline, cancellationToken: Cancellation);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
            await AwaitAssertAsync(() => Assert.True(ConsumedAt(journal, notice.InputId) >= 0,
                "The notice must reach a turn."), Deadline, cancellationToken: Cancellation);
            // The last tool call is recorded before the last model call of the turn. Read the order only after
            // the journal holds the end of the turn, or the end of the turn reads as -1 and the check is false or void.
            var secondTurnEnd = -1;
            await AwaitAssertAsync(() =>
            {
                var events = journal.Committed.ToArray();
                var secondAdmittedAt = Array.FindIndex(events, evt => evt is InputAdmitted admitted && admitted.UserMessage.Content == "probes:two");
                Assert.True(secondAdmittedAt >= 0, "The journal must hold the second input.");
                secondTurnEnd = Array.FindIndex(events, secondAdmittedAt + 1, evt => evt is TurnRecorded);
                Assert.True(secondTurnEnd > secondAdmittedAt, "The journal must hold the end of the second turn.");
            }, Deadline, cancellationToken: Cancellation);
            var noticeAt = ConsumedAt(journal, notice.InputId);
            if (spawnTurnAdopted)
                Assert.True(noticeAt <= secondTurnEnd, $"The notice must join the running turn: consumed at {noticeAt}, the turn ended at {secondTurnEnd}.");
            else
                Assert.True(noticeAt > secondTurnEnd, $"The notice must wait for Bob's turn: consumed at {noticeAt}, the turn ended at {secondTurnEnd}.");
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    /// <summary>
    /// Focused example of the counterexample that <c>ChildNoticeModelTests.Generated_turns_never_mix_requesters</c>
    /// found and shrank on the code of c4ed4dcc6: <c>Spawn(alice) -> ApprovalOpen(bob) -> EndChild(#0,Lost) -> ApprovalClose</c>.
    /// Bob's turn asks for two approvals. Alice's child is lost while the first approval waits. The result of the child
    /// must not change the authority of Bob's turn, so Bob can answer his second approval too.
    /// Planted bug: the result of the child takes over the authority of the running turn at its next tool boundary.
    /// </summary>
    [Fact]
    public async Task A_lost_child_of_another_requester_leaves_the_authority_of_a_two_approval_turn_alone()
    {
        // Arrange
        var sessionId = new SessionId("notice/lost-child-two-approvals");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, _) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        await SendAsync(sessionId, "probe2:p1", Operator(Bob));
        var first = await FishApprovalAsync(subscriber, "probe-p1");

        // Act: Alice's child is lost while Bob's first approval waits.
        var childPath = $"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}/subagent-child-a-{accepted.Run.RunId.Value}";
        Sys.Stop(await Sys.ActorSelection(childPath).ResolveOne(Deadline, Cancellation));
        await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, accepted.Run.RunId),
            "The lost child must produce a result."), Deadline, cancellationToken: Cancellation);
        AssertAck(await AnswerAsync(sessionId, first.CallId, Bob));
        var second = await FishApprovalAsync(subscriber, "probe-p1-b");

        // Assert: the second approval of the turn belongs to Bob, and Bob can answer it.
        Assert.Equal(Bob, second.RequesterSenderId?.Value);
        AssertAck(await AnswerAsync(sessionId, second.CallId, Bob));
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ToolCallRecorded>(),
            evt => evt.ToolResult.ToolCallId?.Value == "probe-p1-b"), Deadline, cancellationToken: Cancellation);
    }

    /// <summary>
    /// Spec 6 (E4). When operator input and a ready child result wait at the same boundary, the operator input reaches the
    /// model in the same model request or in an earlier one. A notice and the text of the same requester share one batch.
    /// The check reads either form of the result, so it fails for the order and not for the form.
    /// The tool-batch case is a guard: the current code already drains the operator text at that boundary.
    /// Planted bug: a child precedence rule runs the child result before the operator input at the end of a turn or batch.
    /// </summary>
    [Theory]
    [InlineData("turn-end")]
    [InlineData("tool-batch")]
    public async Task Buffered_operator_input_reaches_the_model_no_later_than_a_ready_child_result(string boundary)
    {
        // Arrange
        const string operatorText = "Operator follow-up.";
        var sessionId = new SessionId($"notice/order-{boundary}");
        var journal = await JournalAsync();
        var parentBoundary = NewGate();
        Clients.Parent.ResponseContentsSelector = request =>
        {
            var reply = ScriptedParentReply(request);
            if (reply?.OfType<FunctionCallContent>().Any(call => call.Name == "spawn_agent") == true)
            {
                // The model call that follows the spawn result is the boundary under test.
                if (boundary == "turn-end")
                    Clients.Parent.NextResponseGate = parentBoundary;
                return reply;
            }

            if (boundary == "tool-batch" && HasResult(request, "spawn-one") && !HasCall(request, "boundary-probe"))
                return [Probe("boundary-probe")];
            return reply;
        };
        var subscriber = await JoinAsync(sessionId);
        var (accepted, childGate) = await StartChildAsync(sessionId, Alice, "one");
        ToolInteractionRequest? approval = null;
        if (boundary == "tool-batch")
            approval = await FishApprovalAsync(subscriber, "boundary-probe");
        else
            // The journal holds ChildRunAccepted before the spawn tool batch ends. A child that ends in that window
            // gives a notice that the batch boundary sends in model call 2, before the operator text exists.
            // The gate is null after call 2 is recorded and held, so the turn end is the next boundary.
            await AwaitAssertAsync(() => Assert.Null(Clients.Parent.NextResponseGate), Deadline, cancellationToken: Cancellation);

        try
        {
            // Act
            childGate.TrySetResult();
            await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, accepted.Run.RunId),
                "The child result must be ready before the boundary."), Deadline, cancellationToken: Cancellation);
            await SendAsync(sessionId, operatorText, Operator(Alice));
            if (approval is null)
                parentBoundary.TrySetResult();
            else
                AssertAck(await AnswerAsync(sessionId, approval.CallId, Alice));

            // Assert
            await AwaitAssertAsync(() =>
            {
                var requests = Clients.Parent.ReceivedMessages;
                Assert.True(IndexOf(requests, request => HasUserText(request, operatorText)) >= 0, "No request carries the operator text.");
                Assert.True(IndexOf(requests, request => CarriesChildResult(request, accepted.Run.RunId)) >= 0,
                    "No request carries the child result.");
            }, Deadline, cancellationToken: Cancellation);
            var received = Clients.Parent.ReceivedMessages;
            var operatorIndex = IndexOf(received, request => HasUserText(request, operatorText));
            var childIndex = IndexOf(received, request => CarriesChildResult(request, accepted.Run.RunId));
            Note($"boundary={boundary}: first request with the operator text={operatorIndex}, with the child result={childIndex}");
            Assert.True(operatorIndex <= childIndex,
                $"At the {boundary} boundary the child result reached model request {childIndex}; "
                + $"the buffered operator text reached request {operatorIndex}.");
        }
        finally
        {
            childGate.TrySetResult();
            parentBoundary.TrySetResult();
        }
    }

    /// <summary>
    /// Spec 7 (E3). Three ready results of one requester yield three notices. They run in one turn: one model request
    /// carries all three, the model makes three <c>file_read</c> calls, and one turn consumes all three notices.
    /// Planted bug: the runtime activates each notice as its own turn.
    /// </summary>
    [Fact]
    public async Task Three_ready_results_of_one_requester_run_in_one_turn()
    {
        // Arrange
        var sessionId = new SessionId("notice/three-results");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var children = new List<(ChildRunAccepted Accepted, TaskCompletionSource Gate)>();
        foreach (var label in new[] { "one", "two", "three" })
        {
            children.Add(await StartChildAsync(sessionId, Alice, label));
            await ExpectTurnsAsync(subscriber, 1);
        }

        var busy = NewGate();
        Clients.Parent.NextResponseGate = busy;

        try
        {
            // Act: the three results become ready while a turn of the same requester runs.
            await SendAsync(sessionId, "Work on the report.", Operator(Alice));
            foreach (var (_, gate) in children)
                gate.TrySetResult();
            await AwaitAssertAsync(() => Assert.All(children, child => Assert.True(
                IsResultReady(journal, child.Accepted.Run.RunId), $"Run {child.Accepted.Run.RunId.Value} is not ready.")),
                Deadline, cancellationToken: Cancellation);
            busy.TrySetResult();

            // Assert
            var runIds = children.Select(child => child.Accepted.Run.RunId).ToArray();
            await AwaitAssertAsync(() => Assert.True(
                IndexOf(Clients.Parent.ReceivedMessages, request => runIds.All(id => CarriesChildResult(request, id))) >= 0,
                "No single model request carries all three results."), Deadline, cancellationToken: Cancellation);
            var notices = runIds.Select(id => Assert.Single(NoticesFor(journal, id))).ToArray();
            await AwaitAssertAsync(() => Assert.All(notices, notice => Assert.True(ConsumedAt(journal, notice.InputId) >= 0,
                "Every notice must reach a turn.")), Deadline, cancellationToken: Cancellation);
            Assert.Single(notices.Select(notice => ConsumedAt(journal, notice.InputId)).Distinct());
            await AwaitAssertAsync(() => Assert.True(Clients.Parent.ReceivedMessages.Any(request =>
                    Calls(request).Count(call => call.Name == "file_read") == 3
                    && Results(request).Count(result => result.Result?.ToString()?.Contains(ChildOutput, StringComparison.Ordinal) == true) == 3),
                "The model must read three result files with three real file_read calls."), Deadline, cancellationToken: Cancellation);
        }
        finally
        {
            busy.TrySetResult();
            foreach (var (_, gate) in children)
                gate.TrySetResult();
        }
    }

    /// <summary>
    /// Spec 9. Each terminal run yields exactly one notice. A replay and a second recovery add none.
    /// Planted bug: the guard reads the recent source keys, which a restart drops, and not the state of the run.
    /// </summary>
    [Fact]
    public async Task A_run_gets_exactly_one_notice_across_restarts()
    {
        // Arrange
        var sessionId = new SessionId("notice/exactly-once");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        gate.TrySetResult();
        await AwaitAssertAsync(() => Assert.True(NoticesFor(journal, accepted.Run.RunId).Count > 0,
            "The child must yield a notice."), Deadline, cancellationToken: Cancellation);
        await ExpectTurnsAsync(subscriber, 1);

        // Act: two recoveries. Each ends with a turn, so the actor has drained its recovery work before the check.
        for (var restart = 0; restart < 2; restart++)
        {
            var recovered = await RestartSessionAsync(sessionId);
            await SendAsync(sessionId, $"Status check {restart}.", Operator(Alice));
            await ExpectTurnsAsync(recovered, 1);
        }

        // Assert
        Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Single(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(), evt => evt.RunId == accepted.Run.RunId);
        var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
    }

    // A second requester's approvals and the notices share one script: two probes in a row, then the default script.
    private static IReadOnlyList<AIContent>? TwoProbesAndNotices(IReadOnlyList<ChatMessage> request)
    {
        var basic = ScriptedParentReply(request);
        if (!request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User && message.Text == "probes:two"))
            return basic;
        var reply = basic?.ToList() ?? [];
        if (!HasCall(request, "second-turn-probe-1"))
            reply.Add(Probe("second-turn-probe-1"));
        else if (HasResult(request, "second-turn-probe-1") && !HasCall(request, "second-turn-probe-2"))
            reply.Add(Probe("second-turn-probe-2"));
        return reply.Count == 0 ? null : reply;
    }

    private static bool HasUserText(IReadOnlyList<ChatMessage> request, string text)
        => request.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User && message.Text == text);

    private sealed record AuthorityObservation(string CallId, string? Requester, string TurnId, bool Adopted, string AdoptedSpeakers);

    private static AuthorityObservation Observe(string callId, TurnContextRecord? context) => context is null
        ? new AuthorityObservation(callId, "<no turn context>", string.Empty, false, string.Empty)
        : new AuthorityObservation(callId, context.RequesterSenderId?.Value, context.TurnId, context.HasAdoptedContext,
            string.Join(",", context.AdoptedSpeakerIds));
}
