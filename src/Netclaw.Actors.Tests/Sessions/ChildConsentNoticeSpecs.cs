// -----------------------------------------------------------------------
// <copyright file="ChildConsentNoticeSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Task 1.3. The parent learns of child consent requests without a poll. The session keeps one digest and admits it
/// as one approval notice at a model boundary: at once when the session is idle, else at the next tool-batch end or turn
/// end. An answer while another request of the session stays open admits nothing. The last close admits one closing
/// notice. A notice never joins the turn of a person.
/// Source: background-subagent-runs "The parent learns of child consent requests without a poll", netclaw-session
/// "An approval notice is a runtime input of kind child-consent", design D4.
/// </summary>
public sealed class ChildConsentNoticeSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    /// <summary>
    /// Scenario "A fan-out gives one notice for each boundary". Five children of Alice ask for consent while one parent
    /// model call runs. The session admits one notice at the turn end. The notice is automation that the runtime made.
    /// Planted bug: the session admits one notice for each request, admits the notice inside the model call, or copies
    /// the person principal of the run owner.
    /// </summary>
    [Fact]
    public async Task Five_child_requests_in_one_model_call_give_one_notice_at_the_boundary()
    {
        // Arrange: five held children of Alice.
        var sessionId = new SessionId("consent/fan-out");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var children = new List<(ChildRunAccepted Accepted, TaskCompletionSource Gate)>();
        for (var index = 1; index <= 5; index++)
            children.Add(await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), $"ask-{index}"));
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains("Keep going."), hold);

        try
        {
            await SendAsync(sessionId, "Keep going.", Operator(Alice));
            await entered.WaitAsync(Deadline, Cancellation);

            // Act: all five children ask while the parent model call runs.
            foreach (var child in children)
                child.Gate.TrySetResult();
            await FishChildPromptsAsync(subscriber, children.Select(child => child.Accepted.Run.RunId).ToArray());
            await BarrierAsync(sessionId, children[0].Accepted);
            Assert.Empty(ConsentNotices(journal, sessionId));
            hold.TrySetResult();

            // Assert: one notice at the turn end, and no second notice after it.
            await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var notice = Assert.Single(ConsentNotices(journal, sessionId));
            Assert.Equal(PrincipalClassification.VerifiedAutomation, notice.TurnContext.RequesterPrincipal);
            Assert.Equal(new SenderId(NoticeKind), notice.TurnContext.RequesterSenderId);
            Assert.Equal(TransportAuthenticity.LocalProcess, notice.TurnContext.TransportAuthenticity);
            Assert.Null(notice.ChildNotice);
            var text = notice.UserMessage.Content ?? string.Empty;
            Assert.StartsWith(OpenNoticeHeading, text, StringComparison.Ordinal);
            Assert.All(children, child => Assert.Contains($"run={child.Accepted.Run.RunId.Value}", text, StringComparison.Ordinal));
            Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        }
        finally
        {
            hold.TrySetResult();
            foreach (var child in children)
                child.Gate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    /// <summary>
    /// Scenario "An idle parent wakes once". One child asks while the session is idle. The session admits one notice
    /// at once, and the notice starts one turn.
    /// Planted bug: the idle session waits for a later input, or wakes twice for one request.
    /// </summary>
    [Fact]
    public async Task An_idle_session_gets_one_turn_for_one_child_request()
    {
        // Arrange
        var sessionId = new SessionId("consent/idle-wake");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-idle");

        // Act
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, accepted.Run.RunId);

        // Assert
        await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        await ExpectTurnsAsync(subscriber, 1);
        await SettleNoticesAsync(journal, sessionId);
        await BarrierAsync(sessionId, accepted);
        var notice = Assert.Single(ConsentNotices(journal, sessionId));
        Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        Assert.Single(ParentRequestsWith(notice.UserMessage.Content!));
    }

    /// <summary>
    /// Scenario "The last close gives a closing notice". Two requests of the session are open. The answer to the first
    /// admits no notice. The answer to the second admits one closing notice that lists both answers. The held result
    /// of the first child runs after the closing notice.
    /// Planted bug: the session admits a notice for each answer, or starts the held inputs before the closing notice.
    /// </summary>
    [Fact]
    public async Task An_answer_while_another_request_stays_open_admits_no_notice_and_the_last_close_admits_one()
    {
        // Arrange: two children of Alice wait for consent, and the open notices ran.
        var sessionId = new SessionId("consent/last-close");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (first, firstGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-first");
        var (second, secondGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-second");
        firstGate.TrySetResult();
        var firstPrompt = await FishChildPromptAsync(subscriber, first.Run.RunId);
        secondGate.TrySetResult();
        var secondPrompt = await FishChildPromptAsync(subscriber, second.Run.RunId);
        await AwaitAssertAsync(() => Assert.NotEmpty(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        var openNotices = ConsentNotices(journal, sessionId).Count;

        // Act 1: the operator answers the first request. The first child ends, and its result waits.
        AssertAck(await AnswerAsync(sessionId, firstPrompt.CallId, Alice));
        await AwaitAssertAsync(() => Assert.Single(ResultNoticesOf(journal, first.Run.RunId)), Deadline, cancellationToken: Cancellation);
        await BarrierAsync(sessionId, second);

        // Assert 1
        Assert.Equal(openNotices, ConsentNotices(journal, sessionId).Count);
        Assert.Equal(-1, ConsumedAt(journal, ResultNoticesOf(journal, first.Run.RunId)[0].InputId));

        // Act 2: the operator answers the last open request.
        AssertAck(await AnswerAsync(sessionId, secondPrompt.CallId, Alice));

        // Assert 2: one closing notice that lists both answers, and then the held result runs.
        await AwaitAssertAsync(() => Assert.Equal(openNotices + 1, ConsentNotices(journal, sessionId).Count),
            Deadline, cancellationToken: Cancellation);
        var closing = ConsentNotices(journal, sessionId)[^1];
        var text = closing.UserMessage.Content ?? string.Empty;
        Assert.StartsWith(ClosingNoticeHeading, text, StringComparison.Ordinal);
        Assert.Contains($"run={first.Run.RunId.Value} answer=approved_once", text, StringComparison.Ordinal);
        Assert.Contains($"run={second.Run.RunId.Value} answer=approved_once", text, StringComparison.Ordinal);
        Assert.Contains("spawn_agent is available again.", text, StringComparison.Ordinal);
        var heldResult = ResultNoticesOf(journal, first.Run.RunId)[0].InputId;
        await AwaitAssertAsync(() => Assert.True(ConsumedAt(journal, heldResult) >= 0), Deadline, cancellationToken: Cancellation);
        var closingAt = IndexOfEvent(journal, evt => evt is InputAdmitted admitted && admitted.InputId == closing.InputId);
        Assert.True(closingAt < ConsumedAt(journal, heldResult),
            "The session must admit the closing notice before it starts the held inputs.");
        await SettleNoticesAsync(journal, sessionId);
        Assert.Equal(openNotices + 1, ConsentNotices(journal, sessionId).Count);
    }

    /// <summary>
    /// Scenario "Approval notice waits for its own turn". Alice's child asks while Alice's turn runs a tool batch. At the
    /// batch end the notice does not join Alice's turn. It runs in its own turn after Alice's turn ends.
    /// Planted bug: the notice joins the running turn of its owner at the tool-batch boundary, like a child result notice.
    /// </summary>
    [Fact]
    public async Task The_notice_does_not_join_the_turn_of_a_person()
    {
        // Arrange
        var sessionId = new SessionId("consent/no-join");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-join");
        var projectFile = Path.Combine(TestPaths.WorkspacesDirectory, "project", "notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        await File.WriteAllTextAsync(projectFile, "Project notes.", Cancellation);
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains($"read:{projectFile}"), hold);

        try
        {
            await SendAsync(sessionId, $"read:{projectFile}", Operator(Alice));
            var aliceInput = await AwaitInputAdmittedAsync(journal, $"read:{projectFile}");
            await entered.WaitAsync(Deadline, Cancellation);

            // Act: the child asks while Alice's model call runs. Then the model calls file_read, and the batch ends.
            gate.TrySetResult();
            await FishChildPromptAsync(subscriber, accepted.Run.RunId);
            hold.TrySetResult();

            // Assert
            await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var notice = Assert.Single(ConsentNotices(journal, sessionId));
            var noticeText = notice.UserMessage.Content!;
            // Alice's follow-up model call is the first request that holds the result of her file_read call. The later
            // notice turn holds that result in its history too.
            var aliceFollowUp = Clients.Parent.ReceivedMessages.First(request => Results(request)
                .Any(result => result.Result?.ToString()?.Contains("Project notes.", StringComparison.Ordinal) == true));
            Assert.DoesNotContain(aliceFollowUp, message => message.Text.Contains(noticeText, StringComparison.Ordinal));
            var consumer = journal.Committed.Where(evt => ConsumedBy(evt).Contains(notice.InputId)).ToArray();
            Assert.All(consumer, evt => Assert.DoesNotContain(aliceInput.InputId, ConsumedBy(evt)));
            Assert.True(ConsumedAt(journal, notice.InputId) > ConsumedAt(journal, aliceInput.InputId),
                "The notice must run after Alice's turn.");
        }
        finally
        {
            hold.TrySetResult();
            gate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    /// <summary>
    /// Merge rule (design D4, Q1, N2): one digest for the session, and its text is true for the session. Three children
    /// ask, one after the other, while a parent prompt of Alice is open. The session admits a notice at once for each
    /// request, because the turn runs no model call. Each newer notice replaces the waiting notice: the session closes
    /// the waiting notice with InputClosed. After the parent answer, one notice turn runs, and it states three open
    /// requests.
    /// Planted bug: each ready digest stays its own waiting notice, and each newer notice goes in front of the older
    /// notices. The session then runs three notice turns in the order 3, 2, 1, and the model last reads "1 are open".
    /// </summary>
    [Fact]
    public async Task Three_requests_behind_an_open_parent_prompt_give_one_notice_turn_that_states_three_open_requests()
    {
        // Arrange: three held children of Alice, and then an open parent prompt of Alice.
        var sessionId = new SessionId("consent/one-pending-notice-parent-prompt");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var children = new List<(ChildRunAccepted Accepted, TaskCompletionSource Gate)>();
        for (var index = 1; index <= 3; index++)
            children.Add(await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), $"ask-pp{index}"));

        try
        {
            await SendAsync(sessionId, "probe:pp", Operator(Alice));
            var parentPrompt = await FishApprovalAsync(subscriber, "probe-pp");

            // Act: each child asks in turn while the parent prompt is open. Then Alice answers the parent prompt.
            foreach (var child in children)
            {
                child.Gate.TrySetResult();
                await FishChildPromptAsync(subscriber, child.Accepted.Run.RunId);
                await BarrierAsync(sessionId, child.Accepted);
            }

            AssertAck(await AnswerAsync(sessionId, parentPrompt.CallId, Alice));

            // Assert: the journal shows one notice turn. Each other notice closed with no turn.
            await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
                notice => ConsumedByTurn(journal, notice.InputId) > 0), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var notices = ConsentNotices(journal, sessionId);
            var ran = Assert.Single(notices, notice => ConsumedByTurn(journal, notice.InputId) > 0);
            Assert.Equal(1, ConsumptionCount(journal, ran.InputId));
            var text = ran.UserMessage.Content ?? string.Empty;
            Assert.StartsWith($"{OpenNoticeHeading} 3 are open.", text, StringComparison.Ordinal);
            Assert.All(children, child => Assert.Contains($"run={child.Accepted.Run.RunId.Value}", text, StringComparison.Ordinal));
            Assert.All(notices.Where(notice => notice.InputId != ran.InputId), stale =>
            {
                Assert.Equal(0, ConsumedByTurn(journal, stale.InputId));
                Assert.Contains(journal.Committed.OfType<InputClosed>(), evt => evt.InputIds.Contains(stale.InputId));
            });

            // Assert: the model reads one notice, and that notice is true when it runs.
            var noticeRequest = Assert.Single(Clients.Parent.ReceivedMessages, request => FreshUserTexts(request)
                .Any(fresh => fresh.StartsWith(OpenNoticeHeading, StringComparison.Ordinal)));
            Assert.Contains(FreshUserTexts(noticeRequest), fresh => fresh.StartsWith($"{OpenNoticeHeading} 3 are open.", StringComparison.Ordinal));
            Assert.Single(NoticeTextsSeenByModel());
        }
        finally
        {
            foreach (var child in children)
                child.Gate.TrySetResult();
        }
    }

    /// <summary>
    /// Merge rule (design D4, Q1, N2), without a parent prompt. In one turn of Alice, a child asks during the first
    /// model call, so the open notice waits from the end of tool batch 1. Alice answers the child during the second
    /// model call, so the closing notice comes at the end of tool batch 2. The closing notice replaces the waiting open
    /// notice. After the turn, one notice turn runs, and it carries the closing text. No stale notice that says
    /// "spawn_agent is refused" runs after it.
    /// Planted bug: the closing notice goes in front of the open notice, and both run. The model reads the closing
    /// notice, and then the stale open notice that says spawn_agent is refused.
    /// </summary>
    [Fact]
    public async Task A_closing_notice_replaces_the_waiting_open_notice_of_the_same_turn()
    {
        // Arrange: a held child of Alice. Alice's turn makes two tool batches, and a gate holds each model call.
        var sessionId = new SessionId("consent/one-pending-notice-two-batches");
        var journal = await JournalAsync();
        const string twoBatches = "Read the two project files.";
        var projectDirectory = Path.Combine(TestPaths.WorkspacesDirectory, "project");
        Directory.CreateDirectory(projectDirectory);
        var fileOne = Path.Combine(projectDirectory, "one.md");
        var fileTwo = Path.Combine(projectDirectory, "two.md");
        await File.WriteAllTextAsync(fileOne, "File one.", Cancellation);
        await File.WriteAllTextAsync(fileTwo, "File two.", Cancellation);
        Clients.Parent.ResponseContentsSelector = request =>
        {
            if (FreshUserTexts(request).Contains(twoBatches))
                return [ReadFile("batch-1", fileOne)];
            if (HasResult(request, "batch-1") && !HasCall(request, "batch-2"))
                return [ReadFile("batch-2", fileTwo)];
            return ScriptedParentReply(request);
        };
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-batches");
        var holdFirst = NewGate();
        var holdSecond = NewGate();
        var enteredFirst = ParentGate.Arm(request => FreshUserTexts(request).Contains(twoBatches), holdFirst);

        try
        {
            await SendAsync(sessionId, twoBatches, Operator(Alice));
            await enteredFirst.WaitAsync(Deadline, Cancellation);

            // Act 1: the child asks during model call 1. Batch 1 ends, and model call 2 waits at the gate.
            gate.TrySetResult();
            var childPrompt = await FishChildPromptAsync(subscriber, accepted.Run.RunId);
            await BarrierAsync(sessionId, accepted);
            var enteredSecond = ParentGate.Arm(request => HasResult(request, "batch-1") && !HasCall(request, "batch-2"), holdSecond);
            holdFirst.TrySetResult();
            await enteredSecond.WaitAsync(Deadline, Cancellation);
            var open = Assert.Single(ConsentNotices(journal, sessionId));
            Assert.StartsWith(OpenNoticeHeading, open.UserMessage.Content, StringComparison.Ordinal);

            // Act 2: Alice answers the child during model call 2. Batch 2 ends, and the turn ends.
            AssertAck(await AnswerAsync(sessionId, childPrompt.CallId, Alice));
            await BarrierAsync(sessionId, accepted);
            holdSecond.TrySetResult();

            // Assert: the journal shows one notice turn, with the closing text. The open notice closed with no turn.
            await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
                notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)),
                Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var closing = Assert.Single(ConsentNotices(journal, sessionId),
                notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal));
            Assert.Equal(1, ConsumedByTurn(journal, closing.InputId));
            Assert.Equal(0, ConsumedByTurn(journal, open.InputId));
            Assert.Contains(journal.Committed.OfType<InputClosed>(), evt => evt.InputIds.Contains(open.InputId));
            Assert.Single(ConsentNotices(journal, sessionId), notice => ConsumedByTurn(journal, notice.InputId) > 0);

            // Assert: the model reads the closing notice in one turn and never reads the stale open notice.
            Assert.DoesNotContain(Clients.Parent.ReceivedMessages, request => FreshUserTexts(request)
                .Any(fresh => fresh.StartsWith(OpenNoticeHeading, StringComparison.Ordinal)));
            Assert.Single(Clients.Parent.ReceivedMessages, request => FreshUserTexts(request)
                .Any(fresh => fresh.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)));
            Assert.DoesNotContain(Clients.Parent.ReceivedMessages, request => request.Any(message =>
                message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text.Contains("spawn_agent is refused", StringComparison.Ordinal)));
        }
        finally
        {
            holdFirst.TrySetResult();
            holdSecond.TrySetResult();
            gate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    /// <summary>
    /// Merge rule (design D4, Q1), review blocker. A replacing notice keeps the answers of the notice that it replaces.
    /// A parent prompt of Alice is open, so each notice waits. Child A asks: open notice N1 waits. The operator denies
    /// A. That is the last close, so closing notice N2 replaces N1 with the answer of A. Child B asks before N2 runs,
    /// so open notice N3 replaces N2. One notice turn runs. Its text lists B as the one open request, and it gives the
    /// answer of A under "Answers since the last notice:".
    /// Planted bug: the closing notice clears the digest when the session takes it. The replacing open notice then
    /// holds only B, and the model never reads that the operator denied A.
    /// </summary>
    [Fact]
    public async Task A_replacing_open_notice_keeps_the_answers_of_the_closing_notice_that_it_replaces()
    {
        // Arrange: two held children of Alice, and then an open parent prompt of Alice.
        var sessionId = new SessionId("consent/replacing-notice-keeps-answers");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (childA, gateA) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-ra");
        var (childB, gateB) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-rb");

        try
        {
            await SendAsync(sessionId, "probe:pr", Operator(Alice));
            var parentPrompt = await FishApprovalAsync(subscriber, "probe-pr");

            // Act 1: child A asks, and the operator denies A. The closing notice waits behind the parent prompt.
            gateA.TrySetResult();
            var promptA = await FishChildPromptAsync(subscriber, childA.Run.RunId);
            await BarrierAsync(sessionId, childA);
            AssertAck(await DenyAsync(sessionId, promptA, Alice));
            await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
                notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)),
                Deadline, cancellationToken: Cancellation);

            // Act 2: child B asks before the closing notice runs. Then Alice answers the parent prompt.
            gateB.TrySetResult();
            await FishChildPromptAsync(subscriber, childB.Run.RunId);
            await BarrierAsync(sessionId, childB);
            AssertAck(await AnswerAsync(sessionId, parentPrompt.CallId, Alice));

            // Assert: one notice turn runs. Each other notice closed with no turn.
            await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
                notice => ConsumedByTurn(journal, notice.InputId) > 0), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var notices = ConsentNotices(journal, sessionId);
            var ran = Assert.Single(notices, notice => ConsumedByTurn(journal, notice.InputId) > 0);
            Assert.All(notices.Where(notice => notice.InputId != ran.InputId), stale =>
                Assert.Contains(journal.Committed.OfType<InputClosed>(), evt => evt.InputIds.Contains(stale.InputId)));

            // Assert: the notice that runs lists B as open and gives the answer of A.
            var lines = Lines(ran.UserMessage.Content);
            Assert.Equal($"{OpenNoticeHeading} 1 is open.", lines[0]);
            Assert.Contains(lines, line => line.StartsWith($"- agent=child-a run={childB.Run.RunId.Value} tool=", StringComparison.Ordinal));
            var answers = Array.IndexOf(lines, AnswersHeading);
            Assert.True(answers > 0, $"The notice has no line \"{AnswersHeading}\":\n{ran.UserMessage.Content}");
            Assert.Equal($"- agent=child-a run={childA.Run.RunId.Value} answer=refused reason=denied", lines[answers + 1]);
            Assert.Single(NoticeTextsSeenByModel());
        }
        finally
        {
            gateA.TrySetResult();
            gateB.TrySetResult();
        }
    }

    /// <summary>
    /// Merge rule (design D4), at most once. An answer that a notice turn already gave to the model does not come
    /// again. The session is idle, so each notice runs at once. A and B ask. The operator denies A while B stays open,
    /// so no notice comes. C asks: the open notice N3 lists B and C, and it gives the answer of A under "Answers since
    /// the last notice:". N3 runs. The operator then approves B and C. The closing notice gives only the answers of B
    /// and C.
    /// Planted bug: an open notice keeps its answers in the digest after it runs, so the closing notice repeats the
    /// answer of A.
    /// </summary>
    [Fact]
    public async Task An_answer_that_a_notice_turn_gave_does_not_come_again_in_the_next_notice()
    {
        // Arrange: three held children of Alice.
        var sessionId = new SessionId("consent/answers-once");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (childA, gateA) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-oa");
        var (childB, gateB) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-ob");
        var (childC, gateC) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-oc");

        try
        {
            gateA.TrySetResult();
            var promptA = await FishChildPromptAsync(subscriber, childA.Run.RunId);
            await SettleAfterRequestAsync(journal, sessionId, childA);
            gateB.TrySetResult();
            var promptB = await FishChildPromptAsync(subscriber, childB.Run.RunId);
            await SettleAfterRequestAsync(journal, sessionId, childB);

            // Act 1: the operator denies A while B stays open. Then C asks, and its notice runs.
            AssertAck(await DenyAsync(sessionId, promptA, Alice));
            await BarrierAsync(sessionId, childB);
            gateC.TrySetResult();
            var promptC = await FishChildPromptAsync(subscriber, childC.Run.RunId);
            await SettleAfterRequestAsync(journal, sessionId, childC);
            var withAnswer = ConsentNotices(journal, sessionId)[^1];

            // Act 2: the operator approves B, and then C. The last close gives the closing notice.
            AssertAck(await AnswerAsync(sessionId, promptB.CallId, Alice));
            await BarrierAsync(sessionId, childC);
            AssertAck(await AnswerAsync(sessionId, promptC.CallId, Alice));
            await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
                notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)),
                Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);

            // Assert: the closing notice gives B and C, and it does not repeat the answer of A.
            var closing = ConsentNotices(journal, sessionId)[^1].UserMessage.Content ?? string.Empty;
            Assert.StartsWith(ClosingNoticeHeading, closing, StringComparison.Ordinal);
            Assert.Contains($"run={childB.Run.RunId.Value} answer=approved_once", closing, StringComparison.Ordinal);
            Assert.Contains($"run={childC.Run.RunId.Value} answer=approved_once", closing, StringComparison.Ordinal);
            Assert.DoesNotContain($"run={childA.Run.RunId.Value} answer=", closing, StringComparison.Ordinal);

            // Assert: the open notice that ran gave the answer of A once, under the answers heading.
            Assert.True(ConsumedByTurn(journal, withAnswer.InputId) > 0, "The notice of C must run a turn.");
            var lines = Lines(withAnswer.UserMessage.Content);
            Assert.Equal($"{OpenNoticeHeading} 2 are open.", lines[0]);
            var answers = Array.IndexOf(lines, AnswersHeading);
            Assert.True(answers > 0, $"The notice has no line \"{AnswersHeading}\":\n{withAnswer.UserMessage.Content}");
            Assert.Equal($"- agent=child-a run={childA.Run.RunId.Value} answer=refused reason=denied", lines[answers + 1]);
        }
        finally
        {
            gateA.TrySetResult();
            gateB.TrySetResult();
            gateC.TrySetResult();
        }
    }

    /// <summary>
    /// Design D1.3, rule 3. A notice turn that fails counts as given, at most once. The operator answers child A, so
    /// a closing notice runs, and its model call fails. Then child B asks. The new open notice lists B as open and does
    /// not give the answer of A again.
    /// Planted bug: the session puts the answers of a failed notice turn back in the digest, so the next notice repeats
    /// the answer of A.
    /// </summary>
    [Fact]
    public async Task A_failed_notice_turn_counts_as_given_and_its_answers_do_not_come_again()
    {
        // Arrange: two held children of Alice. The model call of the closing notice turn fails once.
        var sessionId = new SessionId("consent/failed-notice-given");
        var journal = await JournalAsync();
        var failed = 0;
        Clients.Parent.ResponseContentsSelector = request =>
            FreshUserTexts(request).Any(text => text.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal))
            && Interlocked.Exchange(ref failed, 1) == 0
                ? throw new IOException("The model call of the closing notice turn fails.")
                : ScriptedParentReply(request);
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (childA, gateA) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-fa");
        var (childB, gateB) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-fb");

        try
        {
            gateA.TrySetResult();
            var promptA = await FishChildPromptAsync(subscriber, childA.Run.RunId);
            await SettleAfterRequestAsync(journal, sessionId, childA);

            // Act 1: the operator answers A. The closing notice turn starts, and its model call fails.
            AssertAck(await AnswerAsync(sessionId, promptA.CallId, Alice));
            await subscriber.FishForMessageAsync<ErrorOutput>(_ => true, Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var closing = Assert.Single(ConsentNotices(journal, sessionId),
                notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal));
            Assert.Contains($"run={childA.Run.RunId.Value} answer=approved_once", closing.UserMessage.Content, StringComparison.Ordinal);
            Assert.Equal(0, ConsumedByTurn(journal, closing.InputId));
            Assert.Contains(journal.Committed.OfType<InputClosed>(), evt => evt.InputIds.Contains(closing.InputId));
            Assert.Contains(Clients.Parent.ReceivedMessages, request => FreshUserTexts(request)
                .Any(text => text.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)));

            // Act 2: child B asks, and its open notice runs.
            gateB.TrySetResult();
            await FishChildPromptAsync(subscriber, childB.Run.RunId);
            await SettleAfterRequestAsync(journal, sessionId, childB);

            // Assert: the new notice lists B as open and does not repeat the answer of A.
            var latest = ConsentNotices(journal, sessionId)[^1];
            var lines = Lines(latest.UserMessage.Content);
            Assert.Equal($"{OpenNoticeHeading} 1 is open.", lines[0]);
            Assert.Contains(lines, line => line.StartsWith($"- agent=child-a run={childB.Run.RunId.Value} tool=", StringComparison.Ordinal));
            Assert.DoesNotContain($"run={childA.Run.RunId.Value} answer=", latest.UserMessage.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(AnswersHeading, lines);
        }
        finally
        {
            gateA.TrySetResult();
            gateB.TrySetResult();
        }
    }

    // The review proposed this heading for the answers in an open notice. Design D4 gives no text for it.
    private const string AnswersHeading = "Answers since the last notice:";

    private static string[] Lines(string? text)
        => (text ?? string.Empty).Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).ToArray();

    private Task<ISessionResponse> DenyAsync(SessionId sessionId, ToolInteractionRequest prompt, string sender)
        => AskManagerAsync<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId, CallId = prompt.CallId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny),
            SenderId = new SenderId(sender)
        });

    // How many turn events consumed the input: a tool batch or a turn record. InputClosed does not count, because a
    // closed input never reaches the model.
    private static int ConsumedByTurn(AcceptanceFault journal, InputId inputId)
        => journal.Committed.ToArray().Count(evt => evt switch
        {
            ToolBatchStarted batch => batch.ConsumedInputIds.Contains(inputId),
            TurnRecorded turn => turn.ConsumedInputIds.Contains(inputId),
            _ => false
        });

    // The distinct approval notice texts that reached the parent model as user messages, in any request.
    private IReadOnlyList<string> NoticeTextsSeenByModel()
        => Clients.Parent.ReceivedMessages
            .SelectMany(static request => request)
            .Where(static message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(static message => message.Text)
            .Where(static text => text.StartsWith(OpenNoticeHeading, StringComparison.Ordinal)
                                  || text.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Task 1.10 (session part). A child prompt lives until it closes, and a parent prompt lives until the turn ends.
    /// The session sets the lifetime when it emits the prompt.
    /// Planted bug: the session emits every prompt with the default lifetime.
    /// </summary>
    [Fact]
    public async Task A_child_prompt_lives_until_closed_and_a_parent_prompt_until_the_turn_ends()
    {
        // Arrange
        var sessionId = new SessionId("consent/prompt-lifetime");
        await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-lifetime");

        // Act
        gate.TrySetResult();
        var childPrompt = await FishChildPromptAsync(subscriber, accepted.Run.RunId);
        await SettleNoticesAsync(await JournalAsync(), sessionId);
        await SendAsync(sessionId, "probe:p1", Operator(Alice));
        var parentPrompt = await FishApprovalAsync(subscriber, "probe-p1");

        // Assert
        Assert.Equal(ToolInteractionLifetime.UntilClosed, childPrompt.Lifetime);
        Assert.Equal(ToolInteractionLifetime.UntilTurnEnd, parentPrompt.Lifetime);
    }
}

/// <summary>
/// Task 1.3, compaction case. A child asks while the session compacts. The session admits the notice at the end of
/// the compaction, and not before.
/// </summary>
public sealed class ChildConsentCompactionNoticeSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);
        // Last registration wins. A usage of 800 tokens crosses the threshold of 750.
        services.AddSingleton(new ModelCapabilities { ModelId = "notice-model", ContextWindowTokens = 1000 });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                CompactionThreshold = 0.75, SnapshotInterval = 0, KeepRecentMessages = 0, TitleGenerationInterval = 0
            }
        });
    }

    /// <summary>
    /// Planted bug: the session admits the notice during the compaction, or forgets the digest when the compaction
    /// ends.
    /// </summary>
    [Fact]
    public async Task A_child_request_during_compaction_gives_a_notice_at_the_compaction_end()
    {
        // Arrange: a held child of Alice, and a turn whose usage starts a compaction that waits on a gate.
        var sessionId = new SessionId("consent/compaction");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-compact");
        var compaction = NewGate();
        Clients.B.ObservationResponseGate = compaction;
        Clients.Parent.PlannedUsageOverrides.Enqueue(new Microsoft.Extensions.AI.UsageDetails
        {
            InputTokenCount = 800, OutputTokenCount = 50, TotalTokenCount = 850
        });

        try
        {
            await SendAsync(sessionId, "A long answer, please.", Operator(Alice));
            await AwaitAssertAsync(() => Assert.Contains(Clients.B.ReceivedMessages, request => request.Any(message =>
                    message.Role == Microsoft.Extensions.AI.ChatRole.System
                    && message.Text.Contains("You are a session summarizer", StringComparison.Ordinal))),
                Deadline, cancellationToken: Cancellation);

            // Act: the child asks while the session compacts.
            gate.TrySetResult();
            await FishChildPromptAsync(subscriber, accepted.Run.RunId);
            await BarrierAsync(sessionId, accepted);
            Assert.Empty(ConsentNotices(journal, sessionId));
            compaction.TrySetResult();

            // Assert
            await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var notice = Assert.Single(ConsentNotices(journal, sessionId));
            Assert.Contains($"run={accepted.Run.RunId.Value}", notice.UserMessage.Content, StringComparison.Ordinal);
            Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
        }
        finally
        {
            compaction.TrySetResult();
            gate.TrySetResult();
        }
    }
}
