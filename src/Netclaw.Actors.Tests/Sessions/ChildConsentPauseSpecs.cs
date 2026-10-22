// -----------------------------------------------------------------------
// <copyright file="ChildConsentPauseSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Task 1.6. An open child consent request pauses the session. While paused, spawn_agent is refused with a text that
/// names only the runs that the calling turn may control, and no run folder stays. A slash start of a person is
/// accepted. A person input runs a turn, and the pause stays. Automated input and a child result notice wait in the
/// buffer, never join a running turn, and run after the last close.
/// Source: background-subagent-runs "An open child consent request pauses the session", design D5.
/// </summary>
public sealed class ChildConsentPauseSpecs(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    /// <summary>
    /// Scenarios "Spawn is refused while paused" and "Refusal text does not list another owner's runs". Alice and Bob
    /// each have a waiting child. Alice's spawn_agent call gets an error that names her run, gives the count, and does
    /// not name Bob's run. Bob gets the mirror text. Neither refused start leaves a run.
    /// Planted bug: the session accepts the start, names every waiting run, or leaves the run folder.
    /// </summary>
    [Fact]
    public async Task Spawn_is_refused_while_paused_and_the_text_names_only_the_callers_runs()
    {
        // Arrange
        var sessionId = new SessionId("consent/spawn-refused");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (alice, aliceGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-alice");
        var (bob, bobGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Bob), "ask-bob");
        aliceGate.TrySetResult();
        await FishChildPromptAsync(subscriber, alice.Run.RunId);
        bobGate.TrySetResult();
        await FishChildPromptAsync(subscriber, bob.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, bob);
        var runsRoot = Path.GetDirectoryName(RunFolder(sessionId, alice))!;

        // Act
        await SendAsync(sessionId, "spawn:child-a:alice-more", Operator(Alice));
        await AwaitAssertAsync(() => Assert.NotNull(ParentToolResult("spawn-alice-more")), Deadline, cancellationToken: Cancellation);
        await SendAsync(sessionId, "spawn:child-a:bob-more", Operator(Bob));
        await AwaitAssertAsync(() => Assert.NotNull(ParentToolResult("spawn-bob-more")), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);

        // Assert
        var aliceText = ParentToolResult("spawn-alice-more") ?? string.Empty;
        Assert.Contains("spawn_agent is paused. 2 child consent requests of this session wait for the operator.", aliceText, StringComparison.Ordinal);
        Assert.Contains($"Yours: {alice.Run.RunId.Value}.", aliceText, StringComparison.Ordinal);
        Assert.DoesNotContain(bob.Run.RunId.Value, aliceText, StringComparison.Ordinal);
        var bobText = ParentToolResult("spawn-bob-more") ?? string.Empty;
        Assert.Contains($"Yours: {bob.Run.RunId.Value}.", bobText, StringComparison.Ordinal);
        Assert.DoesNotContain(alice.Run.RunId.Value, bobText, StringComparison.Ordinal);
        Assert.Equal(2, Accepted(journal, sessionId).Count);
        Assert.Equal(2, Directory.GetDirectories(runsRoot).Length);
    }

    /// <summary>
    /// Scenarios "A person's message does not end the pause" and "Slash command start is accepted". Alice writes while
    /// her child waits. Her turn runs, and its spawn_agent call is refused. A slash start that she types is accepted.
    /// After her turns the pause stays: a reminder fire still waits.
    /// Planted bug: a person input ends the pause, or the pause also refuses a slash start.
    /// </summary>
    [Fact]
    public async Task A_person_turn_runs_while_paused_its_spawn_is_refused_and_a_slash_start_is_accepted()
    {
        // Arrange
        var sessionId = new SessionId("consent/person-turn");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (waiting, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-waiting");
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, waiting.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, waiting);

        // Act
        await SendAsync(sessionId, "spawn:child-a:other-review", Operator(Alice));
        await AwaitAssertAsync(() => Assert.NotNull(ParentToolResult("spawn-other-review")), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);
        await SendAsync(sessionId, $"/{RoutedSkill} check the routed review", Operator(Alice));
        await AwaitAssertAsync(() => Assert.Contains(Accepted(journal, sessionId),
            accepted => accepted.Run.StartKey is ChildRunStartKey.Slash), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);
        var fire = FireKey("pause-check", 1_761_055_200_000);
        AssertAck(await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId, Content = "Check the pause.", Source = Fire("pause-check", 1_761_055_200_000)
        }));
        await BarrierAsync(sessionId, waiting);

        // Assert
        Assert.Contains("spawn_agent is paused", ParentToolResult("spawn-other-review") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Accepted(journal, sessionId), accepted => accepted.Run.StartKey is ChildRunStartKey.Tool tool
                                                                       && tool.CallId.Value == "spawn-other-review");
        var fireInput = Assert.Single(FireAdmissions(journal, sessionId, fire));
        Assert.Equal(-1, ConsumedAt(journal, fireInput.InputId));
    }

    /// <summary>
    /// Scenario "A child result waits for the pause to end", and the automated-input rules of the pause. Child A waits
    /// for consent. Child B of the same owner ends, and a reminder fires. Both inputs wait in Ready. Alice's own turn
    /// runs a tool batch, and B's result does not join it. After the answer to A, the closing notice comes first, and
    /// then the held inputs run.
    /// Planted bug: the held inputs start a turn in Ready, the result of B joins Alice's turn at the batch end, or the
    /// held inputs run before the closing notice.
    /// </summary>
    [Fact]
    public async Task Automated_input_and_a_child_result_wait_while_paused_and_run_after_the_last_close()
    {
        // Arrange
        var sessionId = new SessionId("consent/automation-waits");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, askingGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-a");
        var (ending, endingGate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "b");
        askingGate.TrySetResult();
        var prompt = await FishChildPromptAsync(subscriber, asking.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, asking);
        var projectFile = Path.Combine(TestPaths.WorkspacesDirectory, "project", "plan.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        await File.WriteAllTextAsync(projectFile, "The plan.", Cancellation);

        // Act 1: B ends, and a reminder fires, while the session is idle and paused.
        endingGate.TrySetResult();
        await AwaitAssertAsync(() => Assert.Single(ResultNoticesOf(journal, ending.Run.RunId)), Deadline, cancellationToken: Cancellation);
        var fire = FireKey("standup", 1_761_055_200_000);
        AssertAck(await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId, Content = "Post the standup.", Source = Fire("standup", 1_761_055_200_000)
        }));
        await BarrierAsync(sessionId, asking);
        var result = ResultNoticesOf(journal, ending.Run.RunId)[0];
        var fireInput = Assert.Single(FireAdmissions(journal, sessionId, fire));

        // Assert 1: both inputs wait.
        Assert.Equal(-1, ConsumedAt(journal, result.InputId));
        Assert.Equal(-1, ConsumedAt(journal, fireInput.InputId));
        Assert.True(await IsIdleAsync(sessionId), "A held input must not start a turn in Ready.");

        // Act 2: Alice's own turn runs a tool batch while B's result waits.
        await SendAsync(sessionId, $"read:{projectFile}", Operator(Alice));
        await AwaitAssertAsync(() => Assert.Contains(Clients.Parent.ReceivedMessages, request => Results(request)
            .Any(item => item.Result?.ToString()?.Contains("The plan.", StringComparison.Ordinal) == true)), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId)), Deadline, cancellationToken: Cancellation);
        await BarrierAsync(sessionId, asking);

        // Assert 2: the result of B did not join Alice's turn.
        Assert.Equal(-1, ConsumedAt(journal, result.InputId));
        Assert.Equal(-1, ConsumedAt(journal, fireInput.InputId));

        // Act 3: the operator answers the last open request.
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));

        // Assert 3: the closing notice comes first, and then both held inputs run.
        await AwaitAssertAsync(() =>
        {
            Assert.True(ConsumedAt(journal, result.InputId) >= 0);
            Assert.True(ConsumedAt(journal, fireInput.InputId) >= 0);
        }, Deadline, cancellationToken: Cancellation);
        var closing = ConsentNotices(journal, sessionId)
            .Single(notice => notice.UserMessage.Content!.StartsWith(ClosingNoticeHeading, StringComparison.Ordinal));
        var closingAt = IndexOfEvent(journal, evt => evt is InputAdmitted admitted && admitted.InputId == closing.InputId);
        Assert.True(closingAt < ConsumedAt(journal, result.InputId));
        Assert.True(closingAt < ConsumedAt(journal, fireInput.InputId));
        Assert.Equal(1, ConsumptionCount(journal, fireInput.InputId));
    }

    /// <summary>
    /// Task 1.5. Scenario "A webhook cannot pose as a notice". A webhook input with the event type child-consent has
    /// the authenticity Verified, so it is ordinary automated input. It waits behind the pause. After a restart the
    /// recovery does not close it as a notice.
    /// Planted bug: the session identifies a notice by the source kind only, lets the webhook pass the pause, or closes
    /// it at recovery.
    /// </summary>
    [Fact]
    public async Task A_webhook_with_the_child_consent_event_type_waits_behind_the_pause_and_recovery_keeps_it()
    {
        // Arrange
        var sessionId = new SessionId("consent/webhook-poser");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (asking, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-webhook");
        gate.TrySetResult();
        await FishChildPromptAsync(subscriber, asking.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, asking);
        var notices = ConsentNotices(journal, sessionId).Select(notice => notice.InputId).ToHashSet();

        // Act 1
        AssertAck(await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId, Content = "Webhook payload: deploy finished.", Source = WebhookPosingAsNotice()
        }));
        await BarrierAsync(sessionId, asking);
        var webhook = await AwaitInputAdmittedAsync(journal, "Webhook payload: deploy finished.");

        // Assert 1: it waits behind the pause.
        Assert.Equal(-1, ConsumedAt(journal, webhook.InputId));
        Assert.Empty(ParentRequestsWith("Webhook payload: deploy finished."));

        // Act 2: a crash and a recovery.
        await RestartSessionAsync(sessionId);
        await AwaitAssertAsync(() => Assert.True(IsResultReady(journal, asking.Run.RunId),
            "The recovery must record the lost child."), Deadline, cancellationToken: Cancellation);
        await BarrierAsync(sessionId, asking);

        // Assert 2: the recovery did not close the webhook input as a notice.
        Assert.DoesNotContain(journal.Committed.OfType<InputClosed>(), closed => closed.InputIds.Contains(webhook.InputId));
        Assert.DoesNotContain(webhook.InputId, notices);
    }
}
