// -----------------------------------------------------------------------
// <copyright file="RejectedSlashCommandWaitingInputTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for issue #2587, the live case. A session recovers in Ready with a parked parent approval. A
/// reminder fire arrives and waits behind the approval. The requester then sends a slash command with a typo. The
/// new input of a person closes the approval (AbandonThenActivate), and the session rejects the command. No turn
/// ends, so no turn end starts the waiting fire. The rejected command must start the next waiting input.
/// </summary>
public sealed class RejectedSlashCommandAfterApprovalTests : ApprovalWaitTestBase
{
    private const string Typo = "/typo";
    private const string FireKey = "build-check:1700000000000";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public RejectedSlashCommandAfterApprovalTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        // The session handles a slash command only when a skill registry exists. "/typo" names no skill.
        services.AddSingleton(new SkillRegistry());
    }

    /// <summary>
    /// The waiting fire runs exactly once after the rejected command, under its own authority.
    /// Bug on dev: the rejected command closes its input and stops. The fire stays only in the memory queue, and no
    /// turn runs it.
    /// </summary>
    [Fact]
    public async Task A_rejected_slash_command_starts_the_fire_that_waited_behind_the_closed_approval()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/rejected-slash-starts-waiting-fire");
        var subscriber = await ParkApprovalThenRecoverAsync(sessionId);
        await SendReminderAsync(sessionId, FireKey);

        // Act: the new input of the requester closes the approval, and the session rejects the unknown command.
        await SendAsync(sessionId, Typo, RequesterSource(Requester) with { ExecutableText = Typo });

        // Assert
        await AwaitAssertAsync(
            () => Assert.True(
                FakeChat.ReceivedMessages.Any(request => LastUserText(request) == ReminderText),
                "The fire that waited behind the closed approval must run after the rejected slash command. No model call carried it."),
            Deadline, cancellationToken: TestContext.Current.CancellationToken);
        await ExpectTurnsAsync(subscriber, 1);
        await AssertNoInputStaysPendingAsync(sessionId);

        var fireRequest = Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == ReminderText);
        Assert.Contains(ReminderPromptMarker, SystemText(fireRequest));
        Assert.DoesNotContain(RequesterPromptMarker, SystemText(fireRequest));
        Assert.Equal(0, ToolExecutor.SuccessfulExecutions);
        var events = (await ReadJournalAsync(sessionId)).Select(entry => entry.Payload).ToArray();
        var fire = Assert.Single(events.OfType<InputAdmitted>(), admitted => admitted.SourceMessageId == FireKey);
        Assert.Single(events.OfType<TurnRecorded>(), turn => turn.ConsumedInputIds.Contains(fire.InputId));
    }
}

/// <summary>
/// Guard for issue #2587. The rejected slash command starts the next waiting input. The child consent pause holds
/// automated input, so the start must keep that hold. The held fire runs only after the last consent request closes.
/// </summary>
public sealed class RejectedSlashCommandDuringChildConsentPauseTests(ITestOutputHelper output) : ChildConsentTestBase(output)
{
    /// <summary>
    /// A child of Alice waits for consent, so the session is paused. A reminder fire waits behind the pause. Alice
    /// sends a slash command with a typo, and the session rejects it. The fire does not start a turn while the pause
    /// holds it. After the operator answers, the fire runs once.
    /// Planted bug: the start after the rejected command drains the buffer and ignores the pause.
    /// </summary>
    [Fact]
    public async Task A_rejected_slash_command_does_not_start_a_fire_that_the_pause_holds()
    {
        // Arrange
        var sessionId = new SessionId("consent/rejected-slash-keeps-the-hold");
        var journal = await JournalAsync();
        ScriptParent();
        ScriptChildren();
        var subscriber = await JoinAsync(sessionId);
        var (waiting, gate) = await StartConsentChildAsync(sessionId, subscriber, Operator(Alice), "ask-hold");
        gate.TrySetResult();
        var prompt = await FishChildPromptAsync(subscriber, waiting.Run.RunId);
        await SettleAfterRequestAsync(journal, sessionId, waiting);
        var fire = FireKey("hold-check", FireMs);
        AssertAck(await AskManagerAsync<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId, Content = FireText, Source = Fire("hold-check", FireMs)
        }));
        await BarrierAsync(sessionId, waiting);
        var fireInput = Assert.Single(FireAdmissions(journal, sessionId, fire));
        var requestsBefore = Clients.Parent.ReceivedMessages.Count;

        // Act: the session rejects the unknown command. The ack comes after the close of the command input.
        await SendAsync(sessionId, "/typo", Operator(Alice));
        await BarrierAsync(sessionId, waiting);

        // Assert: the pause still holds the fire.
        Assert.True(await IsIdleAsync(sessionId), "A held input must not start a turn after a rejected slash command.");
        Assert.Equal(-1, ConsumedAt(journal, fireInput.InputId));
        Assert.DoesNotContain(Clients.Parent.ReceivedMessages.Skip(requestsBefore),
            request => request.Any(message => message.Text.Contains(FireText, StringComparison.Ordinal)));

        // The hold is not a loss: after the last consent request closes, the fire runs once.
        AssertAck(await AnswerAsync(sessionId, prompt.CallId, Alice));
        await AwaitAssertAsync(
            () => Assert.True(ConsumedAt(journal, fireInput.InputId) >= 0,
                "The held fire must run after the last consent request closes."),
            Deadline, cancellationToken: Cancellation);
        await SettleNoticesAsync(journal, sessionId);
        Assert.Equal(1, ConsumptionCount(journal, fireInput.InputId));
    }
}
