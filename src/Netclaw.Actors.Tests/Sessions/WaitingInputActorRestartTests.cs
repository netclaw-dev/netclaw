// -----------------------------------------------------------------------
// <copyright file="WaitingInputActorRestartTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: a person's input that waits behind an open approval runs once after a restart. Rows:
/// <list type="bullet">
/// <item>actor-restart: the supervisor restarts the actor (the actor throws, the daemon keeps running). The actor
/// requeues each buffered input before it restarts, and recovery rebuilds the waiting queue from the journal. Both paths
/// hold the same input. The input must run once, not once for each path.</item>
/// <item>expiry-signal: Bob's input waits behind Alice's open approval. The daemon restart takes longer than the
/// reminder lifetime, so the expiry signal arrives (#2659). The open approval owns the next turn, as on the resume path:
/// the signal closes nothing and posts no restart notice while the prompt is open. After Alice's answer, Bob's input
/// runs once.</item>
/// </list>
/// </summary>
public sealed class WaitingInputActorRestartTests : ApprovalWaitTestBase
{
    private const string FollowUpText = "Also show the branch name.";
    private const string Bob = "U-bob";

    public WaitingInputActorRestartTests(ITestOutputHelper output) : base(output)
    {
    }

    [Theory]
    [InlineData("actor-restart")]
    [InlineData("expiry-signal")]
    public async Task A_person_input_that_waits_behind_an_open_approval_runs_once_after_a_restart(string row)
    {
        // Arrange: the approval is open and the session is live. The person's follow-up waits in the buffer.
        var sessionId = new SessionId($"test-channel/restart-keeps-person-input-once-{row}");
        await ParkApprovalAsync(sessionId);
        await SendAsync(sessionId, FollowUpText, RequesterSource(row == "expiry-signal" ? Bob : Requester));
        var before = await ResolveSessionAsync(sessionId);

        // Act
        if (row == "expiry-signal")
            await SendExpirySignalBehindTheApprovalAsync(sessionId);
        else
        {
            // The actor throws. The supervisor restarts it, and a restart keeps the actor identity.
            before.Tell(new WorkingContextSnapshotFatal(new InvalidOperationException("Planned failure for the restart spec.")));
            await JoinNewSubscriberAsync(sessionId);
        }

        // Assert: the follow-up runs once. A stopped actor would leave the input waiting, so the wait below would fail.
        await AwaitAssertAsync(
            () =>
            {
                Assert.Contains(FakeChat.ReceivedMessages, request => LastUserText(request) == FollowUpText);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        if (row == "actor-restart")
            Assert.Equal(before, await ResolveSessionAsync(sessionId));
        await AssertNoInputStaysPendingAsync(sessionId);

        // A subscriber that joins now sees only the output of a later run.
        var late = await JoinNewSubscriberAsync(sessionId);
        await late.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
        var requests = FakeChat.ReceivedMessages;
        Assert.Single(requests, request => LastUserText(request) == FollowUpText);
        Assert.All(requests, request => Assert.True(
            UserTexts(request).Count(text => text == FollowUpText) <= 1,
            "The follow-up must not appear twice in one model request."));
    }

    // The session restarts, and the expiry signal of the stopped work arrives while Alice's prompt is open. The signal
    // must close nothing and post no notice. Alice then approves, and Bob's input runs after her turn.
    private async Task SendExpirySignalBehindTheApprovalAsync(SessionId sessionId)
    {
        await StopSessionAsync(sessionId);
        var subscriber = await JoinNewSubscriberAsync(sessionId);
        var bob = Assert.Single((await ReadJournalAsync(sessionId)).Select(entry => entry.Payload).OfType<InputAdmitted>(),
            input => input.UserMessage.Content == FollowUpText);

        var reply = await SessionManager.Ask<ISessionResponse>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = RestartExpiredInstruction,
            Source = RestartReminderSource(new ReminderDefinition
            {
                Id = new ReminderId("restart-resume-behind-approval"),
                Title = "Resume after daemon restart",
                Instructions = RestartExpiredInstruction,
                Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot },
                Delivery = new ReminderDelivery { Kind = DeliveryKind.CurrentSession, SessionId = sessionId.Value },
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal
            })
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(reply is CommandAck, $"The expiry signal got {reply}.");
        await JoinNewSubscriberAsync(sessionId);

        var events = await ReadJournalAsync(sessionId);
        Assert.False(events.Select(entry => entry.Payload).OfType<InputClosed>().Any(close => close.InputIds.Contains(bob.InputId)),
            "The expiry signal closed Bob's input, which waits behind the open approval.");
        while (subscriber.HasMessages)
        {
            if (subscriber.ReceiveOne(TimeSpan.Zero) is TextOutput text)
                Assert.Fail($"The expiry signal posted text while the approval is open: {text.Text}");
        }

        Answer(sessionId, approve: true);
    }
}
