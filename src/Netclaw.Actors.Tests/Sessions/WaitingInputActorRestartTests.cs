// -----------------------------------------------------------------------
// <copyright file="WaitingInputActorRestartTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: a person's input that waits behind an open approval runs once after the session actor restarts.
/// <para>
/// The supervisor restarts the actor (the actor throws, the daemon keeps running). The actor requeues each buffered
/// input before it restarts, and recovery rebuilds the waiting queue from the journal. Both paths hold the same input.
/// The input must run once, not once for each path.
/// </para>
/// </summary>
public sealed class WaitingInputActorRestartTests : ApprovalWaitTestBase
{
    private const string FollowUpText = "Also show the branch name.";

    public WaitingInputActorRestartTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task A_person_input_that_waits_behind_an_open_approval_runs_once_after_an_actor_restart()
    {
        // Arrange: the approval is open and the session is live. The person's follow-up waits in the buffer.
        var sessionId = new SessionId("test-channel/actor-restart-keeps-person-input-once");
        await ParkApprovalAsync(sessionId);
        await SendAsync(sessionId, FollowUpText, RequesterSource(Requester));
        var before = await ResolveSessionAsync(sessionId);

        // Act: the actor throws. The supervisor restarts it, and a restart keeps the actor identity.
        before.Tell(new WorkingContextSnapshotFatal(new InvalidOperationException("Planned failure for the restart spec.")));
        await JoinNewSubscriberAsync(sessionId);

        // Assert: the follow-up runs once. A stopped actor would leave the input waiting, so the wait below would fail.
        await AwaitAssertAsync(
            () =>
            {
                Assert.Contains(FakeChat.ReceivedMessages, request => LastUserText(request) == FollowUpText);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
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
}
