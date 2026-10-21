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
