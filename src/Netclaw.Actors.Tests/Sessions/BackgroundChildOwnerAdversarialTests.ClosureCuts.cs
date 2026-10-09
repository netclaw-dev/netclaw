// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.ClosureCuts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Fact]
    public async Task A_synchronous_child_dispatch_prefix_cannot_block_the_parent_after_cancel_admission()
    {
        var workerEntered = NewSignal();
        var workerExited = NewSignal();
        using var workerRelease = new ManualResetEventSlim();
        var controlRelease = NewSignal();
        var path = Path.Combine(_directory!.Path, "confirmed-before-held-entry.txt");
        var effects = 0;
        var read = new FakeNetclawTool("file_read", "The actual child receipt is retained.", "file", invocation =>
        {
            var effect = Interlocked.Increment(ref effects);
            if (effect == 2)
            {
                workerEntered.TrySetResult();
                try { workerRelease.Wait(TestContext.Current.CancellationToken); }
                finally { workerExited.TrySetResult(); }
            }
            invocation.TryComplete(new ToolInvocationReceipt.Succeeded([new ToolFileActivity(path, ToolFileActivityKind.Read)], null));
        });
        Host.Services.GetRequiredService<ToolRegistry>().ReplaceCore(read);
        _child.PlannedResponses.Enqueue([ReadCall("confirmed-read")]);
        _child.PlannedResponses.Enqueue([ReadCall("held-read")]);
        var (_, manager, subscriber) = await CreateOwnerAsync();
        Task<object>? statusTask = null;
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Delegate the neutral task.", Source = DeliverySource("original", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            _childRelease.TrySetResult();
            await workerEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
            var checkpoint = Assert.Single(before.OfType<ChildRunEvent.Checkpointed>());
            Assert.Equal(1, checkpoint.Checkpoint.CompletedRound);
            Assert.Equal("The actual child receipt is retained.", checkpoint.Checkpoint.Summary);
            Assert.Equal(path, Assert.Single(checkpoint.Checkpoint.ConfirmedActivity.ReadFiles));
            Assert.Equal(2, _child.CallCount);
            Assert.Equal(2, Volatile.Read(ref effects));
            Assert.Equal("operator-a", read.LastContext!.RunScope.DefaultDeliveryTarget!.DestinationId);
            _start!.ControlCompletionGate = controlRelease;
            _main.PlannedResponses.Enqueue([ControlCall("held-entry-cancel", accepted.Run.RunId)]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Cancel the owned child.", Source = DeliverySource("cancel", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            var cancelled = await _start.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.NotNull(cancelled.Run);
            Assert.Equal(BackgroundChildState.Cancelling, cancelled.Run.State);
            Assert.Null(cancelled.Run.DispatchClosedAtMs);
            Assert.False(workerExited.Task.IsCompleted);
            var admitted = await ReadJournalAsync();
            Assert.Single(admitted.OfType<ChildRunEvent.CancellationRequested>());
            Assert.Empty(admitted.OfType<ChildRunEvent.DispatchClosed>());
            Assert.Empty(admitted.OfType<ChildRunEvent.TerminalRecorded>());
            var control = _start.ControlInvocation!.SpawnChildActor!;
            statusTask = control(new ChildControlRequest(accepted.Run.RunId, false), "status-during-held-prefix", _start.ControlToken);
            var status = Assert.IsType<ChildControlReply>(await statusTask.WaitAsync(Ceiling, TestContext.Current.CancellationToken));
            Assert.NotNull(status.Run);
            Assert.Equal(BackgroundChildState.Cancelling, status.Run.State);
            Assert.Null(status.Run.DispatchClosedAtMs);
            Assert.Equal(checkpoint.Checkpoint, status.Run.ChildCheckpoint);
            Assert.False(workerExited.Task.IsCompleted);
            Assert.Equal(2, _child.CallCount);
        }
        finally
        {
            workerRelease.Set();
            if (workerEntered.Task.IsCompletedSuccessfully)
                await workerExited.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            try
            {
                if (statusTask is not null) await statusTask.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            }
            finally { controlRelease.TrySetResult(); }
        }
        await AwaitAssertAsync(async () =>
        {
            var final = await ReadJournalAsync();
            Assert.Single(final.OfType<ChildRunEvent.DispatchClosed>());
            var terminal = Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
        }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _child.CallCount);
        Assert.Equal(2, Volatile.Read(ref effects));
    }

    [Fact]
    public async Task A_real_checkpoint_ack_cannot_start_a_provider_request_after_local_dispatch_closure()
    {
        const string mailbox = "child-checkpoint-ack-cut-mailbox";
        Sys.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            $"{mailbox} {{ mailbox-type = \"{typeof(BackgroundChildApprovalGrantAdversarialTests.WaitReplyMailbox).AssemblyQualifiedName}\" }}"));
        var gate = BackgroundChildApprovalGrantAdversarialTests.WaitReplyMailbox.Gates.GetOrCreateValue(Sys);
        gate.MessageName = nameof(BackgroundChildCheckpointAck);
        gate.Arm();
        _start!.ChildMailbox = mailbox;
        var closureEntered = NewSignal();
        var closureRelease = NewSignal();
        var nextProvider = NewSignal();
        var effects = 0;
        var path = Path.Combine(_directory!.Path, "confirmed-before-held-ack.txt");
        Host.Services.GetRequiredService<ToolRegistry>().ReplaceCore(new FakeNetclawTool(
            "file_read", "The acknowledged child result is retained.", "file", invocation =>
            {
                Interlocked.Increment(ref effects);
                invocation.TryComplete(new ToolInvocationReceipt.Succeeded([new ToolFileActivity(path, ToolFileActivityKind.Read)], null));
            }));
        _child.ToolCallsOnFirstCall = [ReadCall("checkpoint-read")];
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is ChildRunEvent.DispatchClosed)
            {
                closureEntered.TrySetResult();
                await closureRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return false;
        });
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Delegate the neutral task.", Source = DeliverySource("original", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            _child.NextResponseGate = nextProvider;
            _childRelease.TrySetResult();
            var captured = await gate.Held.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var ack = Assert.IsType<BackgroundChildCheckpointAck>(captured.Envelope.Message);
            Assert.Equal(owner, captured.Envelope.Sender);
            var before = await ReadJournalAsync();
            var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
            var checkpoint = Assert.Single(before.OfType<ChildRunEvent.Checkpointed>());
            Assert.Equal(accepted.Run.RunId, ack.RunId);
            Assert.Equal(checkpoint.Checkpoint.CompletedRound, ack.CompletedRound);
            Assert.Equal(1, Volatile.Read(ref effects));
            Assert.Equal(1, _child.CallCount);
            _main.PlannedResponses.Enqueue([ControlCall("held-ack-cancel", accepted.Run.RunId)]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Cancel the owned child.", Source = DeliverySource("cancel", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            await closureEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var cancel = await _start.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(BackgroundChildState.Cancelling, cancel.Run!.State);
            Assert.Null(cancel.Run.DispatchClosedAtMs);
            Assert.Empty((await ReadJournalAsync()).OfType<ChildRunEvent.DispatchClosed>());
            gate.Release();
            var barrier = await captured.Receiver.Ask<ActorIdentity>(new Identify("after-closed-checkpoint-ack"),
                Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(captured.Receiver, barrier.Subject);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(1, Volatile.Read(ref effects));
            Assert.False(nextProvider.Task.IsCompleted);
            closureRelease.TrySetResult();
            await AwaitAssertAsync(async () =>
            {
                var final = await ReadJournalAsync();
                Assert.Single(final.OfType<ChildRunEvent.DispatchClosed>());
                Assert.Single(final.OfType<ChildRunEvent.Checkpointed>());
                var terminal = Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
                var completion = Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Terminal.Result.Completion);
                Assert.Equal(path, Assert.Single(completion.ConfirmedActivity!.ReadFiles));
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, _child.CallCount);
            Assert.Equal(1, Volatile.Read(ref effects));
        }
        finally
        {
            gate.Release();
            closureRelease.TrySetResult();
            nextProvider.TrySetResult();
        }
    }

    private static FunctionCallContent ReadCall(string id) => new(id, "file_read",
        new Dictionary<string, object?> { ["_rationale"] = "Read the neutral fixture." });
}
