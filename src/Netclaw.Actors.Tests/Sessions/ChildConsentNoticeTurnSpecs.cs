// -----------------------------------------------------------------------
// <copyright file="ChildConsentNoticeTurnSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Task 1.4. The text and the turn of an approval notice. One digest serves the whole session. The notice turn takes the
/// audience, the boundary, and the delivery targets of the owner of the first open request, and it lists the runs of
/// every owner. The closing notice comes only when no request of the session stays open, lists the held reminder
/// fires, and says that spawn_agent is available. The notice turn is automation: a call that needs consent is denied
/// without a prompt, and a turn with no text ends quietly.
/// Source: design D4, netclaw-session "An approval notice is a runtime input of kind child-consent", agent-event-channel
/// "No closing notice while a request stays open".
/// </summary>
public sealed class ChildConsentNoticeTurnSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    /// <summary>
    /// Requests of Alice and Bob open during one parent model call. One notice lists both runs. It carries the audience,
    /// the boundary, and the delivery targets of Alice, who owns the first open request, and a new turn ID.
    /// Planted bug: the session keeps one digest for each owner, takes the targets of the last request, or reuses the
    /// turn ID of the run owner.
    /// </summary>
    [Fact]
    public async Task One_digest_for_two_owners_takes_the_first_owner_context_and_lists_both_runs()
    {
        // Arrange
        var sessionId = new SessionId("consent/two-owners");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (alice, aliceGate) = await StartConsentChildAsync(sessionId, subscriber,
            OperatorWithTargets(Alice, AliceTarget, AliceRequestedTarget), "ask-alice");
        var (bob, bobGate) = await StartConsentChildAsync(sessionId, subscriber, OperatorWithTargets(Bob, BobTarget), "ask-bob");
        var hold = NewGate();
        var entered = ParentGate.Arm(request => FreshUserTexts(request).Contains("Keep going."), hold);

        try
        {
            await SendAsync(sessionId, "Keep going.", OperatorWithTargets(Bob, BobTarget));
            await entered.WaitAsync(Deadline, Cancellation);

            // Act: Alice's child asks first, and then Bob's child asks.
            aliceGate.TrySetResult();
            await FishChildPromptAsync(subscriber, alice.Run.RunId);
            bobGate.TrySetResult();
            await FishChildPromptAsync(subscriber, bob.Run.RunId);
            hold.TrySetResult();

            // Assert
            await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
            await SettleNoticesAsync(journal, sessionId);
            var notice = Assert.Single(ConsentNotices(journal, sessionId));
            var text = notice.UserMessage.Content ?? string.Empty;
            Assert.Contains($"run={alice.Run.RunId.Value}", text, StringComparison.Ordinal);
            Assert.Contains($"run={bob.Run.RunId.Value}", text, StringComparison.Ordinal);
            Assert.Contains("check_agent_run can read the status of a run of this session, or cancel it.", text, StringComparison.Ordinal);
            Assert.Equal(AliceTarget, notice.TurnContext.DefaultDeliveryTarget);
            Assert.Equal(AliceRequestedTarget, notice.TurnContext.RequestedDeliveryTarget);
            Assert.Equal(alice.Run.OriginalContext.Audience, notice.TurnContext.Audience);
            Assert.Equal(alice.Run.OriginalContext.Boundary, notice.TurnContext.Boundary);
            Assert.Equal(alice.Run.OriginalContext.ChannelType, notice.TurnContext.ChannelType);
            Assert.Equal(sessionId, notice.TurnContext.SessionId);
            Assert.NotEqual(alice.Run.OriginalContext.TurnId, notice.TurnContext.TurnId);
            Assert.False(notice.TurnContext.SupportsInteractiveApproval);
        }
        finally
        {
            hold.TrySetResult();
            aliceGate.TrySetResult();
            bobGate.TrySetResult();
            ParentGate.Disarm();
        }
    }

    /// <summary>
    /// Scenario "No closing notice while a request stays open". Alice's answer leaves Bob's request open, so no notice
    /// comes and no text says that spawn_agent is available. Bob's answer closes the last request and admits one
    /// closing notice with both answers.
    /// Planted bug: the session keeps one digest for each owner and sends Alice a closing notice while Bob waits.
    /// </summary>
    [Fact]
    public async Task No_closing_notice_while_Bob_waits_after_Alice_answers()
    {
        // Arrange
        var sessionId = new SessionId("consent/bob-waits");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (alice, aliceGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-alice");
        var (bob, bobGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Bob), "ask-bob");
        aliceGate.TrySetResult();
        var alicePrompt = await FishChildPromptAsync(subscriber, alice.Run.RunId);
        bobGate.TrySetResult();
        var bobPrompt = await FishChildPromptAsync(subscriber, bob.Run.RunId);
        await AwaitAssertAsync(() => Assert.NotEmpty(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        var before = ConsentNotices(journal, sessionId).Count;

        // Act 1
        AssertAck(await AnswerAsync(sessionId, alicePrompt.CallId, Alice));
        await AwaitAssertAsync(() => Assert.Single(ResultNoticesOf(journal, alice.Run.RunId)), Deadline, cancellationToken: Cancellation);
        await BarrierAsync(sessionId, bob);

        // Assert 1
        Assert.Equal(before, ConsentNotices(journal, sessionId).Count);
        Assert.DoesNotContain(ConsentNotices(journal, sessionId),
            notice => notice.UserMessage.Content?.Contains("spawn_agent is available again", StringComparison.Ordinal) == true);

        // Act 2
        AssertAck(await AnswerAsync(sessionId, bobPrompt.CallId, Bob));

        // Assert 2
        await AwaitAssertAsync(() => Assert.Equal(before + 1, ConsentNotices(journal, sessionId).Count),
            Deadline, cancellationToken: Cancellation);
        var closing = ConsentNotices(journal, sessionId)[^1].UserMessage.Content ?? string.Empty;
        Assert.StartsWith(ClosingNoticeHeading, closing, StringComparison.Ordinal);
        Assert.Contains($"run={alice.Run.RunId.Value} answer=approved_once", closing, StringComparison.Ordinal);
        Assert.Contains($"run={bob.Run.RunId.Value} answer=approved_once", closing, StringComparison.Ordinal);
        Assert.Contains("spawn_agent is available again.", closing, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario "The later fire replaces the earlier fire", notice part: the closing notice lists the reminder fire that
    /// waited for the pause, so the parent model knows why the reminder turn follows.
    /// Planted bug: the closing notice omits the held fires, words the line in another way, or puts the line after
    /// "spawn_agent is available again.".
    /// </summary>
    [Fact]
    public async Task The_closing_notice_lists_the_held_reminder_fires()
    {
        // Arrange
        var sessionId = new SessionId("consent/closing-lists-fires");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-fires");
        gate.TrySetResult();
        var prompt = await FishChildPromptAsync(subscriber, accepted.Run.RunId);
        await AwaitAssertAsync(() => Assert.NotEmpty(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        AssertAck(await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId, Content = "Sweep the open pull requests.", Source = Fire("daily-pr-sweep", 1_761_055_200_000)
        }));

        // Act
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));

        // Assert
        await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
            notice => notice.UserMessage.Content?.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal) == true),
            Deadline, cancellationToken: Cancellation);
        var closing = ConsentNotices(journal, sessionId)
            .Last(notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal))
            .UserMessage.Content!;
        Assert.Contains("reminder=daily-pr-sweep", closing, StringComparison.Ordinal);
        Assert.Contains("spawn_agent is available again.", closing, StringComparison.Ordinal);

        // Design D4 text: the held-fires line has the design wording, and it comes just before
        // "spawn_agent is available again.". The notice turn rule is the last line (issue 2653). The fire time
        // 1_761_055_200_000 ms is 14:00:00Z.
        var lines = closing.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).ToArray();
        Assert.Equal(AgentEventRenderer.NoticeTurnRule, lines[^1]);
        Assert.Equal("spawn_agent is available again.", lines[^2]);
        Assert.Equal("Held reminder fires now run: reminder=daily-pr-sweep fired_at=14:00:00Z", lines[^3]);
    }

    /// <summary>
    /// Scenario "Notice turn cannot raise a consent request". In the notice turn the model calls a tool that needs
    /// consent. The context has no interactive approval, so the call is denied without a prompt, and the session never
    /// borrows the requester of the run owner.
    /// Planted bug: the notice turn copies SupportsInteractiveApproval of the run owner and opens a prompt for Alice.
    /// </summary>
    [Fact]
    public async Task A_call_that_needs_consent_in_a_notice_turn_is_denied_without_a_prompt()
    {
        // Arrange: the model calls authority_probe in the notice turn.
        var sessionId = new SessionId("consent/notice-turn-probe");
        var journal = await JournalAsync();
        Clients.Parent.ResponseContentsSelector = request =>
            FreshUserTexts(request).Any(text => text.StartsWith(OpenNoticeHeading, StringComparison.Ordinal))
                ? [Probe("notice-probe")]
                : ScriptedParentReply(request);
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-probe");

        // Act
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, accepted.Run.RunId);

        // Assert
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ToolCallRecorded>(),
            evt => evt.ToolResult.ToolCallId?.Value == "notice-probe"), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        var result = journal.Committed.OfType<ToolCallRecorded>().First(evt => evt.ToolResult.ToolCallId?.Value == "notice-probe");
        Assert.NotEqual("confirmed", result.ToolResult.Content);
        Assert.DoesNotContain(journal.Committed.OfType<ToolApprovalRequested>(), evt => evt.CallId == "notice-probe");
        Assert.Empty(ApprovalTool.Contexts);
        var notice = Assert.Single(ConsentNotices(journal, sessionId));
        Assert.False(notice.TurnContext.SupportsInteractiveApproval);
    }

    /// <summary>
    /// A notice turn whose model writes no text ends quietly. The turn completes with QuietEnd, so a channel posts no
    /// empty-turn fallback, and no error reaches the person.
    /// Planted bug: the empty reply of the notice turn takes the empty-response path and fails the turn with an error.
    /// </summary>
    [Fact]
    public async Task A_notice_turn_with_no_text_ends_quietly()
    {
        // Arrange: the model writes nothing in the notice turn.
        var sessionId = new SessionId("consent/notice-quiet-end");
        var journal = await JournalAsync();
        Clients.Parent.ResponseContentsSelector = request =>
            FreshUserTexts(request).Any(text => text.StartsWith(OpenNoticeHeading, StringComparison.Ordinal))
                ? Array.Empty<AIContent>()
                : ScriptedParentReply(request);
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (accepted, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-quiet");

        // Act
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, accepted.Run.RunId);

        // Assert
        await AwaitAssertAsync(() => Assert.Single(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        var ended = await subscriber.FishForMessageAsync(
            message => message is TurnCompleted or ErrorOutput, Deadline, cancellationToken: Cancellation);
        var completed = Assert.IsType<TurnCompleted>(ended);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.True(completed.QuietEnd, "The notice turn must end quietly, with no empty-turn fallback.");
        await SettleNoticesAsync(journal, sessionId);
    }

    /// <summary>
    /// Live case of 22 October 2026. A child of Alice waits for consent, and the parent cancels it in the notice turn. A
    /// notice turn reads the status of a child run of its own session and cancels it, because a cancel only reduces what
    /// runs. The cancel closes the prompt with the reason "cancelled by parent", and the pause lifts. The notice turn
    /// reaches no run of another session, and another tool keeps the narrow notice authority. A person turn of Bob still
    /// cannot control a run of Alice (#2505).
    /// Planted bug: the notice turn gets "This child run is not available to the requester." for a run of its own
    /// session (today), or the cancel leaves the prompt open and the pause on.
    /// </summary>
    [Theory]
    [InlineData("notice-cancels-own-run")]
    [InlineData("notice-reads-own-run")]
    [InlineData("notice-cancels-run-of-another-session")]
    [InlineData("bob-cancels-alice-run")]
    [InlineData("notice-probe-keeps-notice-authority")]
    public async Task A_notice_turn_controls_the_child_runs_of_its_own_session_only(string row)
    {
        // Arrange: Alice starts a child that asks for consent. The control call runs in the notice turn, or in Bob's turn.
        const string NotAvailable = "Error: This child run is not available to the requester.";
        const string BobText = "Cancel the child of Alice.";
        var callId = $"control-{row}";
        var sessionId = new SessionId($"consent/notice-control-{row}");
        var journal = await JournalAsync();
        string? trigger = null;
        AIContent? call = null;
        Clients.Parent.ResponseContentsSelector = request =>
            trigger is not null && !HasCall(request, callId)
            && FreshUserTexts(request).Any(text => text.StartsWith(trigger, StringComparison.Ordinal))
                ? [call!]
                : ScriptedParentReply(request);
        ScriptChildren();
        var foreignSession = new SessionId($"consent/notice-control-{row}-foreign");
        var foreign = row == "notice-cancels-run-of-another-session"
            ? (await StartConsentChildAsync(foreignSession, await JoinAsync(foreignSession), Operator(Alice), "plain", hold: false)).Accepted
            : null;
        var subscriber = await JoinAsync(sessionId);
        var (alice, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-control");
        var aliceRun = alice.Run.RunId.Value;
        (trigger, call) = row switch
        {
            "notice-cancels-own-run" => (OpenNoticeHeading, CheckRun(callId, aliceRun, cancel: true)),
            "notice-reads-own-run" => (OpenNoticeHeading, CheckRun(callId, aliceRun, cancel: false)),
            "notice-cancels-run-of-another-session" => (OpenNoticeHeading, CheckRun(callId, foreign!.Run.RunId.Value, cancel: true)),
            "bob-cancels-alice-run" => (BobText, CheckRun(callId, aliceRun, cancel: true)),
            "notice-probe-keeps-notice-authority" => (OpenNoticeHeading, (AIContent)Probe(callId)),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, null)
        };
        var cancelsOwnRun = row == "notice-cancels-own-run";

        // Act
        gate.TrySetResult();
        var prompt = await FishChildPromptAsync(subscriber, alice.Run.RunId);
        await AwaitAssertAsync(() => Assert.NotEmpty(ConsentNotices(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        if (row == "bob-cancels-alice-run")
        {
            await SettleNoticesAsync(journal, sessionId);
            await SendAsync(sessionId, BobText, Operator(Bob));
        }

        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ToolCallRecorded>(),
            evt => evt.ToolResult.ToolCallId?.Value == callId), Deadline, cancellationToken: Cancellation);
        if (cancelsOwnRun)
        {
            var closed = await subscriber.FishForMessageAsync<ToolInteractionClosed>(
                item => item.CallId == prompt.CallId, Deadline, cancellationToken: Cancellation);
            Assert.Equal(new ToolInteractionCloseReason.Withdrawn(WithdrawReason.CancelledByParent), closed.Reason);
            await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
                notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)),
                Deadline, cancellationToken: Cancellation);
            await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
                evt => evt.RunId == alice.Run.RunId), Deadline, cancellationToken: Cancellation);
        }

        await SettleNoticesAsync(journal, sessionId);

        // Assert: the shared postcondition. Only the cancel of an own run ends the run, and only it lifts the pause.
        var result = journal.Committed.OfType<ToolCallRecorded>()
            .Single(evt => evt.ToolResult.ToolCallId?.Value == callId).ToolResult.Content ?? string.Empty;
        Note($"row={row} result={result}");
        switch (row)
        {
            case "notice-cancels-own-run" or "notice-reads-own-run":
                Assert.DoesNotContain(NotAvailable, result, StringComparison.Ordinal);
                Assert.Contains($"\"run_id\":\"{aliceRun}\"", result, StringComparison.Ordinal);
                break;
            case "notice-cancels-run-of-another-session" or "bob-cancels-alice-run":
                Assert.Equal(NotAvailable, result);
                break;
            default:
                Assert.NotEqual("confirmed", result);
                Assert.DoesNotContain(journal.Committed.OfType<ToolApprovalRequested>(), evt => evt.CallId == callId);
                break;
        }

        Assert.Equal(cancelsOwnRun ? 1 : 0, journal.Committed.OfType<ChildRunEvent.CancellationRequested>().Count());
        Assert.Equal(cancelsOwnRun ? 1 : 0, journal.Committed.OfType<ChildRunEvent.TerminalRecorded>().Count(evt =>
            evt.RunId == alice.Run.RunId && evt.Terminal.Result.OutcomeReason == SubAgentOutcomeReason.CancelledByParent));
        Assert.Equal(cancelsOwnRun, ConsentNotices(journal, sessionId).Any(notice =>
            notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)));
        Assert.Empty(ApprovalTool.Contexts);
    }

    private static FunctionCallContent CheckRun(string callId, string runId, bool cancel) => new(callId, "check_agent_run",
        new Dictionary<string, object?> { ["runId"] = runId, ["cancel"] = cancel, ["_rationale"] = "Control the child." });
}
