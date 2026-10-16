// -----------------------------------------------------------------------
// <copyright file="ChildNoticeHeldWriteSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Own specs of the S2 implementation, not part of the specs that the test author wrote. They replace the intent of the
/// old test that a held preparation of one child does not stop a ready result of another child.
/// </summary>
public sealed class ChildNoticeHeldWriteSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    /// <summary>
    /// The preparation of child A is held. Child B ends and yields its notice, and the model reads the file of B. After the
    /// release, A yields its own notice. The notice of each run is admitted once, and no run waits for another run.
    /// Planted bug: one scheduler holds every result behind the oldest unready result.
    /// </summary>
    [Fact]
    public async Task A_held_result_write_does_not_hold_back_the_notice_of_another_run()
    {
        // Arrange
        var sessionId = new SessionId("notice/held-write");
        var journal = await JournalAsync();
        ScriptParent();
        var subscriber = await JoinAsync(sessionId);
        var (first, firstGate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);
        var (second, secondGate) = await StartChildAsync(sessionId, Alice, "two");
        await ExpectTurnsAsync(subscriber, 1);
        Preparation.HoldNext();

        try
        {
            // Act: the first child ends and its preparation is held. The second child ends while that preparation waits.
            firstGate.TrySetResult();
            await Preparation.Entered.Task.WaitAsync(Deadline, Cancellation);
            secondGate.TrySetResult();

            // Assert: the notice of the second run is admitted and read while the first run has no notice.
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, second.Run.RunId)), Deadline, cancellationToken: Cancellation);
            await ExpectTurnsAsync(subscriber, 1);
            Assert.Empty(NoticesFor(journal, first.Run.RunId));
            Assert.Contains(Clients.Parent.ReceivedMessages, request => Results(request).Any(result =>
                result.Result?.ToString()?.Contains($"{ChildOutput} two", StringComparison.Ordinal) == true));

            Preparation.Release.TrySetResult();
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, first.Run.RunId)), Deadline, cancellationToken: Cancellation);
            await ExpectTurnsAsync(subscriber, 1);
            Assert.Contains(Clients.Parent.ReceivedMessages, request => Results(request).Any(result =>
                result.Result?.ToString()?.Contains($"{ChildOutput} one", StringComparison.Ordinal) == true));
            Assert.Single(NoticesFor(journal, second.Run.RunId));
        }
        finally
        {
            firstGate.TrySetResult();
            secondGate.TrySetResult();
            Preparation.Release.TrySetResult();
        }
    }
}
