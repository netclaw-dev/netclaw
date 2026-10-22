// -----------------------------------------------------------------------
// <copyright file="ChildConsentReminderSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Task 1.7. A reminder fire into a paused session is admitted, acknowledged, and then it waits. A later fire of the
/// same reminder replaces the waiting fire: the session closes the earlier fire with the existing InputClosed and keeps
/// the later fire. The rule applies while the child pause applies, also when a parent prompt is open. A fire that waits
/// only behind a parent prompt keeps the current drop rule. An isolated reminder session never pauses.
/// Source: netclaw-session "Automated input waits while the session is paused", design D5 "Reminder fires while paused".
/// </summary>
public sealed class ChildConsentReminderSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    private const string Sweep = "pr-sweep";

    /// <summary>
    /// Scenario "The later fire replaces the earlier fire". Reminder R fires at 14:00 and at 15:00 while the session is
    /// paused. The 14:00 fire is closed, the 15:00 fire stays pending, and one turn of R runs after the pause.
    /// Planted bug: the session drops the later fire (today's rule), keeps both fires, or runs a fire during the pause.
    /// </summary>
    [Fact]
    public async Task Two_fires_while_paused_leave_one_pending_fire_and_one_turn_after_the_pause()
    {
        // Arrange
        var sessionId = new SessionId("consent/reminder-replace");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-reminder");
        gate.TrySetResult();
        var prompt = await FishChildPromptAsync(subscriber, asking.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, asking);

        // Act 1
        AssertAck(await SendFireAsync(sessionId, 1_761_055_200_000));
        AssertAck(await SendFireAsync(sessionId, 1_761_058_800_000));
        await BarrierAsync(sessionId, asking);

        // Assert 1: one pending fire, the later one.
        var earlier = Assert.Single(FireAdmissions(journal, sessionId, FireKey(Sweep, 1_761_055_200_000)));
        var later = Assert.Single(FireAdmissions(journal, sessionId, FireKey(Sweep, 1_761_058_800_000)));
        var closed = Assert.Single(journal.Committed.OfType<InputClosed>(), evt => evt.InputIds.Contains(earlier.InputId));
        Assert.Equal([earlier.InputId], closed.InputIds);
        Assert.Equal(1, ConsumptionCount(journal, earlier.InputId));
        Assert.Equal(-1, ConsumedAt(journal, later.InputId));

        // Act 2
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));

        // Assert 2: one turn of R runs, with the later fire.
        await AwaitAssertAsync(() => Assert.True(ConsumedAt(journal, later.InputId) >= 0), Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        Assert.Equal(1, ConsumptionCount(journal, later.InputId));
        Assert.Equal(1, ConsumptionCount(journal, earlier.InputId));
        Assert.Single(journal.Committed.OfType<TurnRecorded>(), turn => turn.SourceReminderId?.Value.StartsWith($"{Sweep}:", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Scenario "Child pause and parent prompt both open". The child pause and a parent prompt of Alice are open. A later
    /// fire replaces the earlier fire. When the child request closes, the fire waits only behind the parent prompt, so
    /// a third fire of R is dropped by the current rule. After the parent answer, one turn of R runs.
    /// Planted bug: the parent prompt hides the child pause, or the replacement rule also applies behind a parent prompt
    /// alone.
    /// </summary>
    [Fact]
    public async Task The_replacement_holds_with_a_parent_prompt_open_and_the_drop_rule_returns_behind_the_parent_prompt_alone()
    {
        // Arrange: a child request and a parent prompt of Alice are open.
        var sessionId = new SessionId("consent/reminder-parent-prompt");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-both");
        gate.TrySetResult();
        var childPrompt = await FishChildPromptAsync(subscriber, asking.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, asking);
        await SendAsync(sessionId, "probe:p1", Operator(Alice));
        var parentPrompt = await FishApprovalAsync(subscriber, "probe-p1");

        // Act 1: two fires while both prompts are open.
        AssertAck(await SendFireAsync(sessionId, 1_761_055_200_000));
        AssertAck(await SendFireAsync(sessionId, 1_761_058_800_000));
        await BarrierAsync(sessionId, asking);

        // Assert 1: the later fire replaced the earlier fire.
        var earlier = Assert.Single(FireAdmissions(journal, sessionId, FireKey(Sweep, 1_761_055_200_000)));
        var later = Assert.Single(FireAdmissions(journal, sessionId, FireKey(Sweep, 1_761_058_800_000)));
        Assert.Contains(journal.Committed.OfType<InputClosed>(), evt => evt.InputIds.Contains(earlier.InputId));
        Assert.Equal(-1, ConsumedAt(journal, later.InputId));

        // Act 2: the child request closes. The parent prompt stays open. A third fire arrives.
        AssertAck(await AnswerAsync(sessionId, childPrompt.CallId, Alice));
        await AwaitAssertAsync(() => Assert.Contains(ConsentNotices(journal, sessionId),
            notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal)),
            Deadline, cancellationToken: Cancellation);
        AssertAck(await SendFireAsync(sessionId, 1_761_062_400_000));
        await BarrierAsync(sessionId, asking);

        // Assert 2: the third fire is dropped, and the later fire still waits.
        Assert.Empty(FireAdmissions(journal, sessionId, FireKey(Sweep, 1_761_062_400_000)));
        Assert.Equal(-1, ConsumedAt(journal, later.InputId));

        // Act 3
        AssertAck(await AnswerAsync(sessionId, parentPrompt.CallId, Alice));

        // Assert 3
        await AwaitAssertAsync(() => Assert.True(ConsumedAt(journal, later.InputId) >= 0), Deadline, cancellationToken: Cancellation);
        Assert.Equal(1, ConsumptionCount(journal, later.InputId));
    }

    /// <summary>
    /// Scenario "An isolated reminder session never pauses". The reminder session has no interactive channel, so its
    /// child is denied without a prompt, and the session does not pause: a later spawn is accepted, and no notice comes.
    /// This spec guards the rule. It passes on dev, because dev never pauses.
    /// Planted bug: the pause counts a request that never opened, or an isolated session gets a notice.
    /// </summary>
    [Fact]
    public async Task An_isolated_reminder_session_never_pauses()
    {
        // Arrange
        var sessionId = new SessionId("reminder/isolated-consent");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);

        // Act
        await SendAsync(sessionId, "spawn:child-a:ask-isolated", Isolated("nightly:1761055200000"));
        await AwaitAssertAsync(() => Assert.Single(Accepted(journal, sessionId)), Deadline, cancellationToken: Cancellation);
        var asking = Accepted(journal, sessionId)[0];
        await AwaitAssertAsync(() => Assert.Contains(journal.Committed.OfType<ChildRunEvent.TerminalRecorded>(),
            evt => evt.RunId == asking.Run.RunId), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);
        await SendAsync(sessionId, "spawn:child-a:second", Isolated("weekly:1761055200000"));
        await AwaitAssertAsync(() => Assert.Equal(2, Accepted(journal, sessionId).Count), Deadline, cancellationToken: Cancellation);

        // Assert
        Assert.DoesNotContain(journal.Committed.OfType<ToolApprovalRequested>(), evt => evt.SourceChildRunId == asking.Run.RunId);
        Assert.Empty(ApprovalTool.Contexts);
        Assert.Empty(ConsentNotices(journal, sessionId));
    }

    private Task<ISessionResponse> SendFireAsync(SessionId sessionId, long fireMs)
        => AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId, Content = "Sweep the open pull requests.", Source = Fire(Sweep, fireMs)
        });

    // The source of a reminder that runs in its own session. The reminder channel has no interactive approval.
    private static MessageSource Isolated(string key) => Reminder(key) with { ChannelType = ChannelType.Reminder };
}
