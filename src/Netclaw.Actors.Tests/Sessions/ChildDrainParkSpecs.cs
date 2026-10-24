// -----------------------------------------------------------------------
// <copyright file="ChildDrainParkSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// running-child-drain-park (issue #2655): a child run that runs at a graceful drain parks at a safe point, and it
/// resumes once after the restart under its recorded authority. Each row checks the drain-park precondition
/// (<see cref="ChildParkTestBase.AssertDrainParkAsync"/>) after the drain acknowledgement, and the postcondition
/// (<see cref="ChildParkTestBase.AssertDrainResumeAsync"/>) after the run ended. The class runs on the virtual
/// scheduler, so a row moves the child wait forward itself.
/// Source: background-subagent-runs and session-resume deltas, design D1 to D7 of running-child-drain-park.
/// </summary>
public sealed class ChildDrainParkSpecs(ITestOutputHelper output) : ChildParkTestBase(output)
{
    private const string BobText = "Bob writes after the restart.";

    protected override bool UseTestScheduler => true;

    /// <summary>
    /// Rows:
    /// <list type="bullet">
    /// <item>batch-end: the drain starts while a held sibling batch runs, and the batch then ends. The child parks with
    /// 0 requests and the result of the call, at round 1. The restart reminder wakes the session, the child resumes
    /// at round 1 with a model call and completes. The sibling ran once.</item>
    /// <item>model-call: the drain starts while the child waits for its first model reply. The child parks at once with
    /// the task message only, and the held reply does not enter the park. After the restart the child resumes with a
    /// model call and completes.</item>
    /// <item>consent: the drain starts during a batch with a sibling call and a consent call. One consent park with 1
    /// request, and no second park. After the restart Alice approves, and the consent call runs once.</item>
    /// <item>automation: a reminder turn with no interactive approval starts the child. The drain park records, the run
    /// does not end, and no prompt shows. The child resumes after the restart and completes.</item>
    /// <item>missed-wait: the sibling stays held past the child wait. One Warning names 1 live child, and the
    /// acknowledgement carries the reminder before the drain deadline. After the restart the run is lost with
    /// owner_restarted, and the sibling ran once. No Parked record.</item>
    /// <item>other-requester: Bob writes after the restart, and his turn is open when the resumed child runs its next
    /// call. That call runs under Alice's recorded authority only.</item>
    /// <item>crash-resumed: the session stops after the Resumed record commits and before the child ends. The next
    /// recovery records the run as lost and starts no child.</item>
    /// </list>
    /// Planted bug: the drain kills a running child, the acknowledgement does not wait for the park record, a parked call
    /// runs twice, a resumed drain park replays its batch, a model reply that came after the park enters the history, an
    /// automation park fails, a stuck child holds the drain past the deadline, the resume takes Bob's authority, or a
    /// crash after the resume record starts the child again.
    /// </summary>
    [Theory]
    [InlineData("batch-end")]
    [InlineData("model-call")]
    [InlineData("consent")]
    [InlineData("automation")]
    [InlineData("missed-wait")]
    [InlineData("other-requester")]
    [InlineData("crash-resumed")]
    public async Task A_running_child_parks_at_a_graceful_drain_and_resumes_after_the_restart(string row)
    {
        // Arrange
        var id = new SessionId($"drain-park/{row}");
        var journal = await JournalAsync();
        ScriptParent();
        var label = $"park-{row}";
        PlanChild(label, row switch
        {
            "consent" => [[Probe("p1"), SiblingCall("s1")]],
            "other-requester" or "crash-resumed" => [[SiblingCall("s1")], [SiblingCall("s2")]],
            _ => [[SiblingCall("s1")]]
        });
        await JoinRecorderAsync(id);
        var firstSibling = row == "model-call" ? Task.CompletedTask : Sibling.HoldNext();
        DrainStart? drain = null;
        var run = await StartAsync(id, label, source: row == "automation" ? Reminder("drain-sweep:1") with { ChannelType = ChannelType.Reminder } : null,
            beforeChildRuns: row != "model-call" ? null : async () =>
            {
                // The child holds the gate, so its first model call waits for the reply when the drain starts.
                await AwaitAssertAsync(() => Assert.Null(Clients.A.NextResponseGate), Deadline, cancellationToken: Cancellation);
                drain = await BeginDrainAsync(id);
            });

        // Act: the drain
        if (row != "model-call")
        {
            await firstSibling.WaitAsync(Deadline, Cancellation);
            drain = await BeginDrainAsync(id);
        }
        if (row == "missed-wait")
        {
            Assert.False(drain!.Ack.IsCompleted, "The session acknowledged the drain while a child runs inside the child wait.");
            await EventFilter.Warning(new Regex(@"child_wait_ended live_children=1$")).ExpectAsync(1,
                () =>
                {
                    AdvanceScheduler(SessionExecutionLifetime.ChildWait);
                    return Task.CompletedTask;
                }, Deadline, Cancellation);
        }
        else
            Sibling.Release();
        var park = await AssertDrainParkAsync(journal, id, run, row switch
        {
            "missed-wait" => null,
            "consent" => 1,
            _ => 0
        }, drain!);
        var reminder = (await drain!.Ack).RestartReminder;
        Sibling.Release();
        switch (row)
        {
            case "batch-end":
                Assert.Equal(1, park.Park!.Park.LoopGuard.ToolIterationCount);
                Assert.Contains(park.Park.Park.Transcript, message => message.Role == ChatRole.Tool && message.ToolCallId?.Value == "s1");
                break;
            case "model-call":
                Assert.Equal(0, park.Park!.Park.LoopGuard.ToolIterationCount);
                Assert.Equal(ChatRole.User, Assert.Single(park.Park.Park.Transcript).Role);
                break;
            case "automation":
                Assert.Empty(PromptsOf(run.Run.RunId));
                break;
        }

        // Act: the restart
        var crashCalls = 0;
        switch (row)
        {
            case "consent":
                await JoinRecorderAsync(id);
                AssertAck(await AnswerAsync(id, new ToolCallId(Assert.Single(RequestsOf(journal, run.Run.RunId)).CallId), Alice));
                break;
            case "other-requester":
                // The resumed child holds its first model call until Bob's turn is open.
                var childGate = NewGate();
                Clients.A.NextResponseGate = childGate;
                var bobHold = NewGate();
                var bobEntered = ParentGate.Arm(request => FreshUserTexts(request).Contains(BobText), bobHold);
                await SendAsync(id, BobText, OperatorWithTargets(Bob, BobTarget));
                await bobEntered.WaitAsync(Deadline, Cancellation);
                await JoinRecorderAsync(id);
                childGate.TrySetResult();
                await AwaitAssertAsync(() => Assert.Contains(Sibling.Calls, static call => call.CallId == "s2"), Deadline,
                    cancellationToken: Cancellation);
                bobHold.TrySetResult();
                break;
            case "crash-resumed":
                var secondRound = Sibling.HoldNext();
                AssertAck(await SendRestartReminderAsync(id, reminder!));
                await secondRound.WaitAsync(Deadline, Cancellation);
                await StopAsync(await ResolveSessionAsync(id));
                crashCalls = Clients.A.CallCount;
                Sibling.Release();
                await JoinRecorderAsync(id);
                break;
            default:
                AssertAck(await SendRestartReminderAsync(id, reminder!));
                await JoinRecorderAsync(id);
                break;
        }

        // Assert
        var terminal = await AssertDrainResumeAsync(journal, id, run, park, probeRuns: row == "consent" ? 1 : 0);
        Assert.Equal(row is "other-requester" or "crash-resumed" ? new[] { "s1", "s2" } : ["s1"],
            Sibling.Calls.Select(static call => call.CallId).ToArray());
        switch (row)
        {
            case "missed-wait" or "crash-resumed":
                Assert.True(terminal.Lost);
                Assert.Equal(SubAgentOutcomeReason.OwnerRestarted, terminal.Result.OutcomeReason);
                if (row == "crash-resumed")
                    Assert.Equal(crashCalls, Clients.A.CallCount);
                break;
            default:
                Assert.True(terminal.Result.Success, terminal.Result.Output);
                break;
        }
        if (row == "consent")
            Assert.Single(journal.Committed.OfType<ChildRunEvent.Parked>(), evt => evt.RunId == run.Run.RunId);
        if (row == "automation")
            Assert.Empty(PromptsOf(run.Run.RunId));
    }
}
