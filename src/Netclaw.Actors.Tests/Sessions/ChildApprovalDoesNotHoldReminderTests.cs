// -----------------------------------------------------------------------
// <copyright file="ChildApprovalDoesNotHoldReminderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec (PR #2505, the requirement that a waiting input runs under its own authority): only a parent approval holds a waiting input. A child approval belongs to the
/// child run, so it must not hold the parent's reminder.
/// <para>
/// The child run asks for an approval while the parent turn is over. A reminder for the parent arrives. It runs as
/// its own turn under the authority of the reminder, and the child approval stays open. The control spec shows that a
/// recovered parent approval does hold the same reminder until the approval closes.
/// </para>
/// </summary>
public sealed class ChildApprovalDoesNotHoldReminderTests : ChildLifetimeTestBase
{
    public ChildApprovalDoesNotHoldReminderTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task A_reminder_runs_as_its_own_turn_while_a_child_approval_is_open()
    {
        // Arrange: the parent starts a child run. The child asks for an approval, and the parent turn ends.
        var sessionId = new SessionId("boundary/child-approval-does-not-hold-reminder");
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

        // Act
        await SendReminderAsync(sessionId, "build-check:1700000000300");

        // Assert: the reminder ran as a turn, and the child approval is still open.
        await ExpectTurnsAsync(subscriber, 1);
        var reminderRequest = Assert.Single(FakeChat.ReceivedMessages, request => LastUserText(request) == ReminderText);
        Assert.Contains(ReminderPromptMarker, SystemText(reminderRequest));
        Assert.Equal(0, First.Runs);

        // The open child approval still takes its answer.
        var reply = await ApproveAsync(sessionId, childApproval!.CallId);
        Assert.IsType<CommandAck>(reply);
        await AwaitAssertAsync(() => Assert.Equal(1, First.Runs), Deadline, cancellationToken: Cancellation);
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
