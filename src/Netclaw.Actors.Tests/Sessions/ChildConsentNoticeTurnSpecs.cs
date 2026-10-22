// -----------------------------------------------------------------------
// <copyright file="ChildConsentNoticeTurnSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
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
            Assert.Contains("check_agent_run does not work in this turn for a run that a person started.", text, StringComparison.Ordinal);
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

        // Design D4 text: the held-fires line has the design wording, and it comes just before the last line,
        // "spawn_agent is available again.". The fire time 1_761_055_200_000 ms is 14:00:00Z.
        var lines = closing.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).ToArray();
        Assert.Equal("spawn_agent is available again.", lines[^1]);
        Assert.Equal("Held reminder fires now run: reminder=daily-pr-sweep fired_at=14:00:00Z", lines[^2]);
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
}
