// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.Cancellation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_checkpoint_commit_precedes_child_progress_and_cancel_retains_its_actual_evidence(bool reportFailure)
    {
        var path = Path.Combine(_directory!.Path, "confirmed-child-file.txt");
        var toolCalls = 0;
        var read = new FakeNetclawTool("file_read", "The actual child result is retained.", "file", invocation =>
        {
            Interlocked.Increment(ref toolCalls);
            invocation.TryComplete(new ToolInvocationReceipt.Succeeded([new ToolFileActivity(path, ToolFileActivityKind.Read)], null));
        });
        Host.Services.GetRequiredService<ToolRegistry>().ReplaceCore(read);
        _child.ToolCallsOnFirstCall = [new FunctionCallContent("child-read-1", "file_read", new Dictionary<string, object?> { ["_rationale"] = "Read the neutral fixture." })];
        var checkpointEntered = NewSignal();
        var checkpointRelease = NewSignal();
        var closureEntered = NewSignal();
        var closureRelease = NewSignal();
        var nextChildResponse = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is ChildRunEvent.Checkpointed)
            {
                checkpointEntered.TrySetResult();
                await checkpointRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            if (record.Payload is ChildRunEvent.DispatchClosed)
            {
                closureEntered.TrySetResult();
                await closureRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return false;
        });
        var (_, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Delegate the neutral task.", Source = DeliverySource("original", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            _child.NextResponseGate = nextChildResponse;
            _childRelease.TrySetResult();
            await checkpointEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
            Assert.Equal(1, _child.CallCount);
            Assert.Empty((await ReadJournalAsync()).OfType<ChildRunEvent.Checkpointed>());
            checkpointRelease.TrySetResult();
            await AwaitAssertAsync(() => Assert.Equal(2, _child.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
            var checkpoint = Assert.Single(before.OfType<ChildRunEvent.Checkpointed>());
            Assert.Equal(accepted.Run.RunId, checkpoint.RunId);
            Assert.Equal(1, checkpoint.Checkpoint.CompletedRound);
            Assert.Equal("The actual child result is retained.", checkpoint.Checkpoint.Summary);
            Assert.Equal(path, Assert.Single(checkpoint.Checkpoint.ConfirmedActivity.ReadFiles));
            Assert.Equal("operator-a", read.LastContext!.RunScope.DefaultDeliveryTarget!.DestinationId);
            var childPairs = _child.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>());
            Assert.Equal("The actual child result is retained.", Assert.IsType<string>(Assert.Single(childPairs).Result));
            var storage = Assert.IsType<ToolSessionScope.Bound>(_start!.Prepared!.Execution.Scope.Authority.Session).Storage;
            var report = Path.Combine(storage.ArtifactDirectory.Value, "cancelled-results.json");
            if (reportFailure) Directory.CreateDirectory(report);
            _main.PlannedResponses.Enqueue([ControlCall("cancel-1", accepted.Run.RunId)]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Cancel this child.", Source = DeliverySource("cancel", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            await closureEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var control = await _start.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.NotNull(control.Run);
            Assert.Equal(BackgroundChildState.Cancelling, control.Run.State);
            Assert.NotNull(control.Run.CancellationRequestedAtMs);
            Assert.Null(control.Run.DispatchClosedAtMs);
            var admitted = await ReadJournalAsync();
            Assert.Single(admitted.OfType<ChildRunEvent.CancellationRequested>());
            Assert.Empty(admitted.OfType<ChildRunEvent.DispatchClosed>());
            Assert.Empty(admitted.OfType<ChildRunEvent.TerminalRecorded>());
            closureRelease.TrySetResult();
            ChildRunEvent.TerminalRecorded? terminal = null;
            await AwaitAssertAsync(async () =>
            {
                terminal = Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.TerminalRecorded>());
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(terminal);
            Assert.False(terminal.Terminal.Lost);
            Assert.False(terminal.Terminal.Result.Success);
            Assert.Equal(SubAgentRunOutcome.Failed, terminal.Terminal.Result.Outcome);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
            var cancelled = Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Terminal.Result.Completion);
            Assert.Equal(path, Assert.Single(cancelled.ConfirmedActivity!.ReadFiles));
            Assert.True(nextChildResponse.Task.IsCanceled);
            Assert.Equal(2, _child.CallCount);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
            if (reportFailure)
            {
                Assert.Contains("No report path is confirmed", terminal.Terminal.EvidenceWarning, StringComparison.Ordinal);
                Assert.DoesNotContain(report, terminal.Terminal.Result.Output, StringComparison.Ordinal);
                Assert.False(File.Exists(report));
            }
            else
            {
                using var body = JsonDocument.Parse(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
                Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
                Assert.Equal("Cancelled", body.RootElement.GetProperty("state").GetString());
                Assert.Equal(checkpoint.Checkpoint.Summary, body.RootElement.GetProperty("summary").GetString());
                Assert.Contains(report, terminal.Terminal.Result.Output, StringComparison.Ordinal);
                Assert.Null(terminal.Terminal.EvidenceWarning);
            }
            var final = await ReadJournalAsync();
            Assert.Single(final.OfType<ChildRunEvent.DispatchClosed>());
            Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Equal(2, _child.CallCount);
        }
        finally
        {
            checkpointRelease.TrySetResult();
            closureRelease.TrySetResult();
            nextChildResponse.TrySetResult();
        }
    }

    [Fact]
    public async Task A_genuine_foreign_parent_tool_cannot_inspect_or_cancel_an_older_child()
    {
        var (_, manager, subscriber) = await CreateOwnerAsync();
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = Session, Content = "Delegate the neutral task.", Source = DeliverySource("original", "operator-a") },
            Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var before = await ReadJournalAsync();
        var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
        _main.PlannedResponses.Enqueue([ControlCall("foreign-cancel", accepted.Run.RunId)]);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = Session, Content = "Inspect that child.", Source = DeliverySource("foreign", "operator-b") },
            Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var denied = await _start!.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        Assert.Null(denied.Run);
        var after = await ReadJournalAsync();
        Assert.Empty(after.OfType<ChildRunEvent.CancellationRequested>());
        Assert.Empty(after.OfType<ChildRunEvent.DispatchClosed>());
        Assert.Empty(after.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.Single(after.OfType<ChildRunAccepted>());
        Assert.False(_childRelease.Task.IsCompleted);
        Assert.Equal(1, _child.CallCount);
        var result = Assert.Single(after.OfType<ToolCallRecorded>(), evt => evt.ToolResult.ToolCallId == new ToolCallId("foreign-cancel"));
        using var body = JsonDocument.Parse(result.ToolResult.Content);
        Assert.False(body.RootElement.GetProperty("found").GetBoolean());
        Assert.DoesNotContain(accepted.Run.RunId.Value, result.ToolResult.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(accepted.Run.ScopeId.Value, result.ToolResult.Content, StringComparison.Ordinal);
        _main.PlannedResponses.Enqueue([ControlCall("own-cancel", accepted.Run.RunId)]);
        await manager.Ask<CommandAck>(new SendUserMessage
        { SessionId = Session, Content = "Cancel the owned child.", Source = DeliverySource("own", "operator-a") },
            Ceiling, TestContext.Current.CancellationToken);
        await AwaitAssertAsync(async () =>
        {
            Assert.Single((await ReadJournalAsync()).OfType<ChildRunEvent.TerminalRecorded>());
        }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        var final = await ReadJournalAsync();
        Assert.Single(final.OfType<ChildRunEvent.CancellationRequested>());
        Assert.Single(final.OfType<ChildRunEvent.DispatchClosed>());
        Assert.Equal(1, _child.CallCount);
    }

    private static FunctionCallContent ControlCall(string id, SubAgentRunId runId) => new(id, "control_probe",
        new Dictionary<string, object?> { ["run_id"] = runId.Value, ["_rationale"] = "Control the recorded child." });
}
