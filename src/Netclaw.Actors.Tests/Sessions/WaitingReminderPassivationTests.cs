// -----------------------------------------------------------------------
// <copyright file="WaitingReminderPassivationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec for tool-authorization TA-11: an unanswered approval request does not keep the session in memory.
/// A reminder that waits behind the approval does not change that rule. The session passivates when it is idle.
/// After the session wakes, the approval and the waiting reminder both remain.
/// </summary>
public sealed class WaitingReminderPassivationTests : ApprovalWaitTestBase
{
    public WaitingReminderPassivationTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task An_idle_session_with_a_waiting_reminder_passivates_and_both_survive()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/passivation-keeps-waiting-reminder");
        await ParkApprovalThenRecoverAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000002");
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);

        // Act: the idle timeout fires. The spec sends the message itself, so no timer races it.
        session.Tell(ReceiveTimeout.Instance);

        // Assert: the session leaves memory even though a reminder waits.
        await ExpectTerminatedAsync(session, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var woken = await JoinNewSubscriberAsync(sessionId);
        Answer(sessionId, approve: true);
        await ExpectTurnsAsync(woken, 1);
        AssertApprovalClosedUnderRequester(approved: true);
        await ExpectTurnsAsync(woken, 1);
        AssertReminderRanOnceAfterApproval();
        await AssertNoInputStaysPendingAsync(sessionId);
    }
}
