// -----------------------------------------------------------------------
// <copyright file="ChildApprovalWithdrawalSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable specs for PR 2 of the change <c>child-approval-events-and-parent-pause</c> (design D6 and D7, tasks 4.1
/// and 4.3). When a child run is cancelled or lost, or when its prompt cannot be shown, the session closes each open
/// prompt of the run: it emits one <see cref="ToolInteractionClosed"/> for each open call, and a later click gets the
/// true reason with no run ID and no agent name. A text answer resolves a prompt only when the sender can answer exactly
/// one open prompt. Source: tool-authorization TA-12, background-subagent-runs "A closed child prompt is withdrawn on
/// every surface", and channel-binding-parity "Shared approval-response flow".
/// <para>
/// The child of each spec makes two or one <c>authority_probe</c> calls in one model response. Each call asks for
/// consent, so each call opens one child prompt. The specs click only after the run ended, so the reply does not race
/// the end of the run.
/// </para>
/// </summary>
public sealed class ChildApprovalWithdrawalSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    private const string Agent = "child-a";

    /// <summary>
    /// A cancel withdraws both open prompts of the run. Each call gets exactly one closed output with the reason
    /// "cancelled by parent", also when the terminal record of the run closes the run a second time.
    /// Planted bug: the cancel removes the entries and tells nobody (today), or the terminal record emits a second
    /// closed output for a call that the cancel closed.
    /// </summary>
    [Fact]
    public async Task A_cancel_closes_each_open_prompt_of_the_run_once()
    {
        // Arrange
        var sessionId = new SessionId("withdraw/cancel-two-prompts");
        var opened = await OpenChildPromptsAsync(sessionId, "cancel-p1", "cancel-p2");

        // Act
        await CancelAsync(sessionId, opened.Accepted);
        var closed = await CollectClosedUntilTheRunEndsAsync(opened.Subscriber);

        // Assert
        Assert.Equal(
            opened.Prompts.Select(prompt => prompt.CallId.Value).Order(StringComparer.Ordinal),
            closed.Select(item => item.CallId.Value).Order(StringComparer.Ordinal));
        Assert.All(closed, item => Assert.Equal(new ToolInteractionCloseReason.Withdrawn(WithdrawReason.CancelledByParent), item.Reason));
        Assert.Empty(ApprovalTool.Contexts);
    }

    /// <summary>
    /// The channel cannot post the buttons of a child prompt and answers with the <c>prompt_unavailable</c> key. The
    /// session withdraws the request with the reason "prompt unavailable", and the call does not run.
    /// Planted bug: the session records the key as an ordinary refusal and emits no closed output (today).
    /// </summary>
    [Fact]
    public async Task A_child_prompt_that_the_channel_cannot_show_is_closed_as_unavailable()
    {
        // Arrange
        var sessionId = new SessionId("withdraw/prompt-unavailable");
        var opened = await OpenChildPromptsAsync(sessionId, "unavailable-p1");
        var prompt = opened.Prompts[0];

        // Act
        await AskManagerAsync<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = prompt.CallId,
            SelectedKey = ApprovalOptionKeys.PromptUnavailableKey,
            SenderId = new SenderId(Alice)
        });

        // Assert
        var closed = await opened.Subscriber.FishForMessageAsync<ToolInteractionClosed>(
            item => item.CallId == prompt.CallId, Deadline, cancellationToken: Cancellation);
        Assert.Equal(new ToolInteractionCloseReason.Withdrawn(WithdrawReason.PromptUnavailable), closed.Reason);
        Assert.Empty(ApprovalTool.Contexts);
    }

    /// <summary>
    /// A click on a withdrawn prompt gets the text of its reason. The text names no run and no agent. The click runs
    /// nothing and writes no grant, and the nack is not the "expired" nack, so the Mattermost synchronous reply does not
    /// say "expired" either. The restart reason is in
    /// <c>SubAgentSpawnIntegrationTests.Spawn_agent_subagent_approval_is_withdrawn_after_parent_session_recovery</c>,
    /// and the "child ended" reason, which only a journal can show, is in <c>RecoveredApprovalLogTests</c>.
    /// Planted bug: the click gets "That approval prompt has expired — the session moved on or restarted" (today), or
    /// the reply names the run, or the click runs the call.
    /// </summary>
    [Theory]
    [InlineData(WithdrawReason.CancelledByParent)]
    [InlineData(WithdrawReason.PromptUnavailable)]
    public async Task A_late_click_on_a_withdrawn_prompt_gets_its_reason_and_runs_nothing(WithdrawReason reason)
    {
        // Arrange
        var sessionId = new SessionId($"withdraw/late-click-{reason}");
        var opened = await OpenChildPromptsAsync(sessionId, $"late-{reason}");
        var prompt = opened.Prompts[0];
        switch (reason)
        {
            case WithdrawReason.CancelledByParent:
                await CancelAsync(sessionId, opened.Accepted);
                break;
            case WithdrawReason.PromptUnavailable:
                await AskManagerAsync<ISessionResponse>(new ToolInteractionResponse
                {
                    SessionId = sessionId, CallId = prompt.CallId,
                    SelectedKey = ApprovalOptionKeys.PromptUnavailableKey, SenderId = new SenderId(Alice)
                });
                break;
        }

        await CollectClosedUntilTheRunEndsAsync(opened.Subscriber);

        // Act
        var reply = await AnswerAsync(sessionId, prompt.CallId, Alice);

        // Assert
        var notice = await opened.Subscriber.FishForMessageAsync<TextOutput>(
            output => ChildWithdrawalTexts.IsApprovalReply(output.Text), Deadline, cancellationToken: Cancellation);
        Assert.Equal(ChildWithdrawalTexts.For(reason), notice.Text);
        var nack = Assert.IsType<CommandNack>(reply);
        Assert.NotEqual(ApprovalNackReasons.PromptExpired, nack.Reason);
        Assert.DoesNotContain(opened.Accepted.Run.RunId.Value, notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Agent, notice.Text, StringComparison.Ordinal);
        Assert.Empty(ApprovalTool.Contexts);
        Assert.DoesNotContain(opened.Journal.Committed.OfType<ToolApprovalResolved>(), resolved =>
            resolved.CallId == prompt.CallId.Value
            && ConsentAnswerCodec.FromJournalText(resolved.Decision) is ConsentAnswer.Grant);
    }

    /// <summary>
    /// Task 4.3 on the session. Two child prompts of Alice are open. Her text answer "A" resolves neither, and the session
    /// tells her to use the buttons. After a button answers one prompt, one prompt stays open, and the next text answer
    /// resolves it as before.
    /// Planted bug: a text answer resolves the latest prompt that the sender can answer (today), so a text that the
    /// model wrote about one command can approve another command.
    /// </summary>
    [Fact]
    public async Task A_text_answer_with_two_open_prompts_resolves_neither()
    {
        // Arrange
        var sessionId = new SessionId("withdraw/text-guard");
        var opened = await OpenChildPromptsAsync(sessionId, "guard-p1", "guard-p2");

        // Act
        var guarded = await AskManagerAsync<ISessionResponse>(TextAnswer(sessionId, "A"));

        // Assert
        Assert.IsType<CommandNack>(guarded);
        var notice = await opened.Subscriber.FishForMessageAsync<TextOutput>(
            output => ChildWithdrawalTexts.IsApprovalReply(output.Text), Deadline, cancellationToken: Cancellation);
        Assert.Equal(ChildWithdrawalTexts.TextAnswerGuard(2), notice.Text);
        Assert.Empty(ApprovalTool.Contexts);
        Assert.DoesNotContain(opened.Journal.Committed.OfType<ToolApprovalResolved>(),
            resolved => opened.Prompts.Any(prompt => prompt.CallId.Value == resolved.CallId));

        // One prompt stays open after a button answer. A text answer then resolves it.
        AssertAck(await AnswerAsync(sessionId, opened.Prompts[0].CallId, Alice));
        AssertAck(await AskManagerAsync<ISessionResponse>(TextAnswer(sessionId, "A")));
        await AwaitAssertAsync(() => Assert.Equal(2, ApprovalTool.Contexts.Count), Deadline,
            cancellationToken: Cancellation);
        Assert.Contains(opened.Journal.Committed.OfType<ToolApprovalResolved>(),
            resolved => resolved.CallId == opened.Prompts[1].CallId.Value);
    }

    /// <summary>
    /// Review fix S4 of PR 2. Two subscribers (two SignalR or TUI clients) show the same child prompt. A child prompt
    /// has the lifetime "until closed", so no turn end removes it from a client. When one client answers the prompt, the
    /// session tells every subscriber that the prompt closed, so the other client does not keep a dead prompt. The close
    /// has the typed reason "answered", not a withdrawal. No text on the second subscriber says that the prompt was
    /// withdrawn or that nothing ran, because the call ran.
    /// Planted bug: only a withdrawal emits <see cref="ToolInteractionClosed"/> (today), so the second subscriber keeps
    /// the answered prompt until its process ends; or the close uses a withdrawal reason, so a channel redraws an
    /// approved call as "withdrawn ... Nothing ran".
    /// </summary>
    [Fact]
    public async Task An_answer_from_one_subscriber_closes_the_child_prompt_on_another_subscriber()
    {
        // Arrange
        var sessionId = new SessionId("withdraw/answered-on-another-subscriber");
        var second = await JoinAsync(sessionId);
        var opened = await OpenChildPromptsAsync(sessionId, "answered-p1");
        var prompt = opened.Prompts[0];
        await FishApprovalAsync(second, prompt.CallId.Value);

        // Act
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));

        // Assert
        var texts = new List<string>();
        var output = await second.FishForMessageAsync<SessionOutput>(item =>
        {
            if (item is TextOutput text)
                texts.Add(text.Text);
            return item is ToolInteractionClosed closedItem && closedItem.CallId == prompt.CallId;
        }, Deadline, cancellationToken: Cancellation);
        var closed = Assert.IsType<ToolInteractionClosed>(output);
        Assert.IsType<ToolInteractionCloseReason.Answered>(closed.Reason);
        Assert.DoesNotContain(texts, text => text.Contains("Nothing ran", StringComparison.OrdinalIgnoreCase)
            || text.Contains("withdrawn", StringComparison.OrdinalIgnoreCase));
        await AwaitAssertAsync(() => Assert.Single(ApprovalTool.Contexts), Deadline, cancellationToken: Cancellation);
    }

    private sealed record OpenedPrompts(
        AcceptanceFault Journal,
        Akka.TestKit.TestProbe Subscriber,
        ChildRunAccepted Accepted,
        IReadOnlyList<ToolInteractionRequest> Prompts);

    // Alice starts child-a. The first model response of the child holds one probe call for each id, so the child opens
    // one prompt for each call in parallel.
    private async Task<OpenedPrompts> OpenChildPromptsAsync(SessionId sessionId, params string[] childCalls)
    {
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        Clients.A.PlannedResponses.Enqueue(childCalls.Select(call => (AIContent)Probe(call)).ToArray());
        await SendAsync(sessionId, $"spawn:{Agent}:withdraw", Operator(Alice));

        var prompts = new List<ToolInteractionRequest>();
        await subscriber.FishForMessageAsync<ToolInteractionRequest>(request =>
        {
            if (request.ToolName.Value == "authority_probe")
                prompts.Add(request);
            return prompts.Count == childCalls.Length;
        }, Deadline, cancellationToken: Cancellation);
        var accepted = Assert.Single(Accepted(journal, sessionId));
        Assert.All(prompts, prompt =>
            Assert.StartsWith($"{accepted.Run.RunId.Value}/subagent-approval/", prompt.CallId.Value, StringComparison.Ordinal));
        return new OpenedPrompts(journal, subscriber, accepted, prompts);
    }

    private async Task CancelAsync(SessionId sessionId, ChildRunAccepted accepted)
    {
        var session = await ResolveSessionAsync(sessionId);
        await session.Ask<string>(new ChildRunControl(accepted.Run.RunId, true, accepted.Run.OriginalContext),
            Deadline, Cancellation);
    }

    // The session emits SubAgentOutput Completed in the callback of the terminal record, after it closed the prompts of
    // the run. Every closed output of the run arrives before it.
    private async Task<IReadOnlyList<ToolInteractionClosed>> CollectClosedUntilTheRunEndsAsync(Akka.TestKit.TestProbe subscriber)
    {
        var closed = new List<ToolInteractionClosed>();
        await subscriber.FishForMessageAsync<SessionOutput>(output =>
        {
            if (output is ToolInteractionClosed item)
                closed.Add(item);
            return output is SubAgentOutput { Phase: SubAgentPhase.Completed };
        }, Deadline, cancellationToken: Cancellation);
        return closed;
    }

    private static ToolInteractionTextResponse TextAnswer(SessionId sessionId, string text) => new()
    {
        SessionId = sessionId,
        Text = text,
        SenderId = new SenderId(Alice)
    };
}
