// -----------------------------------------------------------------------
// <copyright file="DeferredIdlePassivationLiveChildTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: the deferred passivation after an idle abandonment must keep a session that has a live child
/// run. The normal idle path keeps such a session, and the deferred path must follow the same rule.
/// <para>
/// A model reply holds two gated tool calls. The person answers one, and a reminder waits behind the other. The idle
/// timeout abandons the batch and runs the reminder. The reminder turn starts a child run. When that turn ends, the
/// session must stay in memory, so the child result reaches the parent. After the child result turn, the normal idle
/// rule applies and the session passivates. The control spec shows that the session passivates at once when the
/// reminder turn starts no child run.
/// </para>
/// </summary>
public sealed class DeferredIdlePassivationLiveChildTests : ChildLifetimeTestBase
{
    public DeferredIdlePassivationLiveChildTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task The_session_stays_in_memory_while_the_reminder_turn_leaves_a_live_child()
    {
        // Arrange
        var sessionId = new SessionId("boundary/deferred-idle-live-child");
        var childGate = NewGate();
        Clients.Child.NextResponseGate = childGate;
        FakeChat.PlannedResponses.Enqueue([Spawn("reminder-spawn", "child-a")]);
        var (session, subscriber) = await ReachIdleAbandonmentAsync(sessionId);

        try
        {
            // Act: the idle timeout abandons the batch. The reminder turn starts the child run and ends.
            session.Tell(ReceiveTimeout.Instance);
            await ExpectTurnsAsync(subscriber, 1);
            await Clients.Child.FirstCallEntered.Task.WaitAsync(Deadline, Cancellation);

            // Assert: the live child keeps the session. The child result reaches the parent.
            childGate.TrySetResult();
            await AwaitAssertAsync(
                () => Assert.Contains(FakeChat.ReceivedMessages, HasChildResult),
                Deadline,
                cancellationToken: Cancellation);
            Assert.Equal(0, First.Runs + Second.Runs);

            // Act: the child result turn ends, and the session gets an idle timeout with no live child.
            await ExpectTurnsAsync(subscriber, 1);
            session.Tell(ReceiveTimeout.Instance);

            // Assert: with no live child, the normal idle rule applies.
            await ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        }
        finally
        {
            childGate.TrySetResult();
        }
    }

    // Control: the same flow without a child run. The deferred passivation ends the session when the reminder turn ends.
    [Fact]
    public async Task The_session_passivates_when_the_reminder_turn_starts_no_child()
    {
        // Arrange
        var sessionId = new SessionId("boundary/deferred-idle-no-child");
        var (session, _) = await ReachIdleAbandonmentAsync(sessionId);

        // Act
        session.Tell(ReceiveTimeout.Instance);

        // Assert
        await ExpectTerminatedAsync(session, Deadline, cancellationToken: Cancellation);
        Assert.Contains(FakeChat.ReceivedMessages, request => LastUserText(request) == ReminderText);
        Assert.Equal(0, Clients.Child.CallCount);
    }

    // A half-answered batch, a recovered session, and a reminder that waits. The caller sends the idle message.
    private async Task<(IActorRef Session, Akka.TestKit.TestProbe Subscriber)> ReachIdleAbandonmentAsync(SessionId sessionId)
    {
        FakeChat.ToolCallsOnFirstCall =
        [
            Probe("call-first", FirstTool),
            Probe("call-second", SecondTool)
        ];
        var first = await JoinFullAsync(sessionId);
        await SendRequesterAsync(sessionId, "Run both probes.");
        var approvals = new List<ToolInteractionRequest>();
        await first.FishForMessageAsync(message =>
        {
            if (message is ToolInteractionRequest request)
                approvals.Add(request);
            return approvals.Count == 2;
        }, Deadline, cancellationToken: Cancellation);
        var firstApproval = approvals.Single(request => request.ToolName.Value == FirstTool);

        await StopSessionAsync(sessionId);
        var recovered = await JoinFullAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000400");
        Assert.IsType<CommandAck>(await ApproveAsync(sessionId, firstApproval.CallId));

        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        return (session, recovered);
    }
}
