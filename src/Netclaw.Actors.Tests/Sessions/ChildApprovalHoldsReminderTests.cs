// -----------------------------------------------------------------------
// <copyright file="ChildApprovalHoldsReminderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec (child-approval-events-and-parent-pause, design D5): an open child consent request pauses the
/// session, so it holds the parent's reminder like an open parent approval does. This spec replaces the PR #2505 rule
/// that only a parent approval holds a waiting input.
/// <para>
/// The child run asks for an approval while the parent turn is over. A reminder for the parent arrives. It waits
/// while the child approval is open, and it runs as its own turn under the authority of the reminder after the answer.
/// The control spec shows that a recovered parent approval holds the same reminder until the approval closes.
/// </para>
/// </summary>
public sealed class ChildApprovalHoldsReminderTests : ChildLifetimeTestBase
{
    private const string PersonBarrierText = "Give me the status.";

    public ChildApprovalHoldsReminderTests(ITestOutputHelper output) : base(output)
    {
    }

    // The ID of the one admitted input with this exact text.
    private static InputId AdmittedInputId(IReadOnlyList<JournalEntry> events, string text)
        => Assert.Single(events.Select(static entry => entry.Payload).OfType<InputAdmitted>(),
            admitted => admitted.UserMessage.Content == text).InputId;

    [Fact]
    public async Task A_reminder_waits_for_an_open_child_approval_and_runs_after_it_closes()
    {
        // Arrange: the parent starts a child run. The child asks for an approval, and the parent turn ends.
        var sessionId = new SessionId("boundary/child-approval-holds-reminder");
        FakeChat.ToolCallsOnFirstCall = [Spawn("spawn-child", "child-a")];
        Clients.Child.ToolCallsOnFirstCall = [Probe("child-call", FirstTool)];
        var subscriber = await JoinFullAsync(sessionId);
        await SendRequesterAsync(sessionId, "Start the child run.");

        var sawChildApproval = false;
        var sawParentTurnEnd = false;
        ToolInteractionRequest? childApproval = null;
        await subscriber.FishForMessageAsync(message =>
        {
            if (message is ToolInteractionRequest { ToolName.Value: FirstTool } request)
            {
                childApproval = request;
                sawChildApproval = true;
            }

            if (message is TurnCompleted { Outcome: TurnOutcome.Completed })
                sawParentTurnEnd = true;

            return sawChildApproval && sawParentTurnEnd;
        }, Deadline, cancellationToken: Cancellation);

        // Act 1: the reminder arrives while the child approval is open. The session acknowledges it after the journal
        // stores it, and the reminder waits.
        await SendReminderAsync(sessionId, "build-check:1700000000300");

        // Barrier: a person input starts its own turn while the session is paused. A reminder turn that started at
        // the admission ends before that person turn, so the journal shows its consumption when the person input
        // is consumed.
        await SendRequesterAsync(sessionId, PersonBarrierText);
        await AwaitAssertAsync(async () =>
        {
            var events = await ReadJournalAsync(sessionId);
            Assert.DoesNotContain(AdmittedInputId(events, PersonBarrierText), PendingInputIds(events));
        }, Deadline, cancellationToken: Cancellation);

        // Assert 1: the journal holds the reminder as pending input, and no model request holds its text.
        var held = await ReadJournalAsync(sessionId);
        Assert.Contains(AdmittedInputId(held, ReminderText), PendingInputIds(held));
        Assert.DoesNotContain(FakeChat.ReceivedMessages, request => UserTexts(request).Contains(ReminderText));
        Assert.Equal(0, First.Runs);

        // Act 2: the operator answers the child approval.
        var reply = await ApproveAsync(sessionId, childApproval!.CallId);
        Assert.IsType<CommandAck>(reply);

        // Assert 2: the child call runs, and the reminder runs once as its own turn under its own authority.
        await AwaitAssertAsync(() => Assert.Equal(1, First.Runs), Deadline, cancellationToken: Cancellation);
        await AwaitAssertAsync(() =>
        {
            var reminderRequest = Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == ReminderText);
            Assert.Contains(ReminderPromptMarker, SystemText(reminderRequest));
        }, Deadline, cancellationToken: Cancellation);
    }

    // Control: a recovered parent approval holds the same reminder until the approval closes. The session is in
    // Ready after the recovery, as in the spec above, so only the owner of the approval differs.
    [Fact]
    public async Task A_reminder_waits_for_a_recovered_parent_approval_and_runs_after_it_closes()
    {
        // Arrange: the parent asks for an approval, and the session restarts with the approval still open.
        var sessionId = new SessionId("boundary/recovered-parent-approval-holds-reminder");
        FakeChat.ToolCallsOnFirstCall = [Probe(CallId, FirstTool)];
        var first = await JoinFullAsync(sessionId);
        await SendRequesterAsync(sessionId, "Run the probe.");
        var approval = await first.FishForMessageAsync<ToolInteractionRequest>(
            request => request.ToolName.Value == FirstTool, Deadline, cancellationToken: Cancellation);
        await StopSessionAsync(sessionId);
        var subscriber = await JoinFullAsync(sessionId);

        // Act: the reminder arrives, and then the person approves.
        await SendReminderAsync(sessionId, "build-check:1700000000301");
        Assert.IsType<CommandAck>(await ApproveAsync(sessionId, approval.CallId));

        // Assert: the approved call runs first. The reminder runs after it as its own turn under its own authority.
        await ExpectTurnsAsync(subscriber, 2);
        Assert.Equal(1, First.Runs);
        AssertReminderRanOnceAfterApproval();
    }
}
