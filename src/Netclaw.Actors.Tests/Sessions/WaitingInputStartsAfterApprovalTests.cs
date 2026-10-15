// -----------------------------------------------------------------------
// <copyright file="WaitingInputStartsAfterApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Persistence;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: an approval can close with no re-drive of its tool batch. Then no tool turn ends and drains the
/// buffer. The close itself must start the turn of the input that waits.
/// <para>
/// The spec makes the case with a journal that holds an approval request and no tool batch. The answer then finds no
/// unanswered tool call to re-drive, and the session reports the prompt as expired.
/// </para>
/// </summary>
public sealed class WaitingInputStartsAfterApprovalTests : ApprovalWaitTestBase
{
    public WaitingInputStartsAfterApprovalTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task A_waiting_reminder_starts_its_turn_when_the_approval_closes_without_a_redrive()
    {
        // Arrange: copy a real approval request into a journal that has no tool batch.
        var source = new SessionId("test-channel/expired-approval-source");
        await ParkApprovalAsync(source);
        var request = (await ReadJournalAsync(source)).Select(entry => entry.Payload)
            .OfType<ToolApprovalRequested>().Single();
        await StopSessionAsync(source);

        var sessionId = new SessionId("test-channel/expired-approval-target");
        await SeedJournalAsync(sessionId, request with
        {
            SessionId = sessionId,
            TurnContext = request.TurnContext! with { SessionId = sessionId }
        });
        var subscriber = await JoinNewSubscriberAsync(sessionId);
        await SendReminderAsync(sessionId, "build-check:1700000000003");

        // Act
        var reply = await SessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(request.CallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(Requester)
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: the prompt expired, and the reminder ran as its own turn.
        Assert.Equal(ApprovalNackReasons.PromptExpired, Assert.IsType<CommandNack>(reply).Reason);
        await ExpectTurnsAsync(subscriber, 1);
        var reminderRequest = Assert.Single(FakeChat.ReceivedMessages, call => LastUserText(call) == ReminderText);
        Assert.Contains(ReminderPromptMarker, SystemText(reminderRequest));
        Assert.Equal(0, ToolExecutor.SuccessfulExecutions);
    }

    private async Task SeedJournalAsync(SessionId sessionId, ISessionEvent evt)
    {
        var seeder = Sys.ActorOf(Props.Create(() => new JournalSeeder($"session-{sessionId.Value}")));
        await seeder.Ask<Akka.Done>(evt, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class JournalSeeder : ReceivePersistentActor
    {
        public JournalSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<object>(_ => { });
            Command<ISessionEvent>(evt =>
            {
                var replyTo = Sender;
                Persist(evt, _ => replyTo.Tell(Akka.Done.Instance));
            });
        }

        public override string PersistenceId { get; }
    }
}
