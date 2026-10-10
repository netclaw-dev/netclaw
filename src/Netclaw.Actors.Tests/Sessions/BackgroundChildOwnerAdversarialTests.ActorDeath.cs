// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.ActorDeath.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Fact]
    public async Task Real_child_actor_death_records_one_failure_and_preserves_parent_status_tools()
    {
        var (owner, manager, subscriber) = await CreateObligationOwnerAsync();
        await SendObligationInputAsync(manager, "original", "operator-a");
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedObligationTurnAsync(subscriber);
        var before = await ReadJournalAsync();
        var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
        Assert.Equal(accepted.Run.RunId, Assert.Single(before.OfType<ChildRunEvent.Started>()).RunId);
        Assert.Empty(before.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.False(_childRelease.Task.IsCompleted);
        var child = await Sys.ActorSelection($"{owner.Path}/child-run-{accepted.Run.RunId.Value}")
            .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
        var watcher = CreateTestProbe();
        watcher.Watch(child);
        Sys.Stop(child);
        await watcher.ExpectTerminatedAsync(child, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await AwaitAssertAsync(async () =>
        {
            var recorded = Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Equal(accepted.Run.RunId, recorded.RunId);
            Assert.Equal(SubAgentOutcomeReason.ActorStopped, recorded.Terminal.Result.OutcomeReason);
        }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await CompletedObligationTurnAsync(subscriber);
        var settled = await ReadJournalPositionsAsync();
        var terminalRow = Assert.Single(settled, record => record.Event is ChildRunEvent.TerminalRecorded);
        var terminal = Assert.IsType<ChildRunEvent.TerminalRecorded>(terminalRow.Event);
        Assert.Equal(accepted.Run.RunId, terminal.RunId);
        Assert.Equal(terminalRow.SequenceNr, terminal.TerminalSequenceNr);
        Assert.False(terminal.Terminal.Lost);
        Assert.False(terminal.Terminal.Result.Success);
        Assert.Equal(SubAgentOutcomeReason.ActorStopped, terminal.Terminal.Result.OutcomeReason);
        Assert.IsType<ChildRunCompletion.Failed>(terminal.Terminal.Result.Completion);
        var delivery = Assert.Single(settled.Select(record => record.Event).OfType<ChildRunEvent.DeliveryAdmitted>());
        Assert.Equal(accepted.Run.RunId, delivery.RunId);
        Assert.Single(settled.Select(record => record.Event).OfType<TurnRecorded>(),
            turn => turn.ConsumedInputIds.Contains(delivery.Input.InputId));

        const string statusCallId = "status-after-child-actor-death";
        _main.PlannedResponses.Enqueue([new FunctionCallContent(statusCallId, "status_json_probe",
            new Dictionary<string, object?>
            {
                ["run_id"] = accepted.Run.RunId.Value, ["cancel"] = false,
                ["_rationale"] = "Inspect the child after its actor stops."
            })]);
        await SendObligationInputAsync(manager, statusCallId, "operator-a");
        var output = Assert.IsType<ToolResultOutput>(await subscriber.FishForMessageAsync<object>(
            message => message is ToolResultOutput result && result.CallId.Value == statusCallId,
            Ceiling, cancellationToken: TestContext.Current.CancellationToken));
        using (var body = JsonDocument.Parse(output.Result))
        {
            Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal("Failed", body.RootElement.GetProperty("state").GetString());
            Assert.False(body.RootElement.GetProperty("cancellation_requested").GetBoolean());
        }
        await CompletedObligationTurnAsync(subscriber);
        var final = await ReadJournalAsync();
        var status = Assert.Single(final.OfType<ToolCallRecorded>(),
            record => record.ToolResult.ToolCallId == new ToolCallId(statusCallId));
        Assert.Equal(output.Result, status.ToolResult.Content);
        Assert.Single(final.OfType<ChildRunAccepted>());
        Assert.Single(final.OfType<ChildRunEvent.Started>());
        Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.Single(final.OfType<ChildRunEvent.DeliveryAdmitted>());
        Assert.Equal(owner, await OwnerAsync());
        Assert.Equal(1, _child.CallCount);
        Assert.False(_childRelease.Task.IsCompletedSuccessfully);
    }
}
