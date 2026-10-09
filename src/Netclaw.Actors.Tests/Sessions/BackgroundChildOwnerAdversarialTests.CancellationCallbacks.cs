// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.CancellationCallbacks.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_held_execution_cancellation_callback_cannot_block_the_owner_terminal(bool naturalCompletion)
    {
        var callbackEntered = NewSignal();
        var callbackExited = NewSignal();
        using var callbackRelease = new ManualResetEventSlim();
        var provider = new UncooperativeChildClient(_child)
        {
            CancellationCallback = () =>
            {
                callbackEntered.TrySetResult();
                try { callbackRelease.Wait(TestContext.Current.CancellationToken); }
                finally { callbackExited.TrySetResult(); }
            }
        };
        Assert.IsType<RoleProvider>(Host.Services.GetRequiredService<IChatClientProvider>()).Child = provider;
        var path = Path.Combine(_directory!.Path, "confirmed-before-held-cancellation-callback.txt");
        var effects = 0;
        Host.Services.GetRequiredService<ToolRegistry>().ReplaceCore(new FakeNetclawTool(
            "file_read", "The real receipt precedes the callback hold.", "file", invocation =>
            {
                Interlocked.Increment(ref effects);
                invocation.TryComplete(new ToolInvocationReceipt.Succeeded([new ToolFileActivity(path, ToolFileActivityKind.Read)], null));
            }));
        _child.ToolCallsOnFirstCall = [ReadCall("before-held-cancellation-callback")];
        var controlRelease = NewSignal();
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
            await provider.Held.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
            var checkpoint = Assert.Single(before.OfType<ChildRunEvent.Checkpointed>());
            Assert.Equal(path, Assert.Single(checkpoint.Checkpoint.ConfirmedActivity.ReadFiles));
            Assert.Equal(new SenderId("operator-a"), accepted.Run.OriginalContext.RequesterSenderId);
            var pair = Assert.Single(provider.HeldMessages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal("before-held-cancellation-callback", pair.CallId);
            Assert.Equal(checkpoint.Checkpoint.Summary, Assert.IsType<string>(pair.Result));
            Assert.False(provider.Token.IsCancellationRequested);
            Assert.False(callbackEntered.Task.IsCompleted);
            if (naturalCompletion)
            {
                provider.Pending.TrySetResult(new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "The neutral child task is complete.")));
            }
            else
            {
                _start!.ControlCompletionGate = controlRelease;
                _main.PlannedResponses.Enqueue([ControlCall("cancel-held-callback", accepted.Run.RunId)]);
                await manager.Ask<CommandAck>(new SendUserMessage
                { SessionId = Session, Content = "Cancel the accepted child.", Source = DeliverySource("cancel", "operator-a") },
                    Ceiling, TestContext.Current.CancellationToken);
                var cancelled = await _start.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                Assert.Equal(BackgroundChildState.Cancelling, cancelled.Run!.State);
            }
            await callbackEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.True(provider.Token.IsCancellationRequested);
            Assert.False(callbackExited.Task.IsCompleted);
            ChildRunEvent.TerminalRecorded? terminal = null;
            await AwaitAssertAsync(async () =>
            {
                var records = await ReadJournalAsync();
                Assert.Single(records.OfType<ChildRunAccepted>());
                Assert.Single(records.OfType<ChildRunEvent.Started>());
                terminal = Assert.Single(records.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Equal(accepted.Run.RunId, terminal.RunId);
                if (naturalCompletion)
                {
                    Assert.Empty(records.OfType<ChildRunEvent.CancellationRequested>());
                    Assert.Empty(records.OfType<ChildRunEvent.DispatchClosed>());
                    Assert.True(terminal.Terminal.Result.Success);
                    Assert.IsType<ChildRunCompletion.Completed>(terminal.Terminal.Result.Completion);
                    Assert.Equal("The neutral child task is complete.", terminal.Terminal.Result.Output);
                }
                else
                {
                    Assert.Single(records.OfType<ChildRunEvent.CancellationRequested>());
                    Assert.Single(records.OfType<ChildRunEvent.DispatchClosed>());
                    Assert.False(terminal.Terminal.Result.Success);
                    Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
                    var cancelled = Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Terminal.Result.Completion);
                    Assert.Equal(path, Assert.Single(cancelled.ConfirmedActivity!.ReadFiles));
                }
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(terminal);
            if (naturalCompletion)
            {
                _main.PlannedResponses.Enqueue([new FunctionCallContent("status-held-callback", "status_json_probe",
                    new Dictionary<string, object?> { ["run_id"] = accepted.Run.RunId.Value, ["cancel"] = false,
                        ["_rationale"] = "Inspect the owned child terminal." })]);
                await manager.Ask<CommandAck>(new SendUserMessage
                { SessionId = Session, Content = "Read the owned child status.", Source = DeliverySource("status", "operator-a") },
                    Ceiling, TestContext.Current.CancellationToken);
                await AwaitAssertAsync(async () =>
                {
                    var records = await ReadJournalAsync();
                    var result = Assert.Single(records.OfType<ToolCallRecorded>(), record => record.ToolResult.ToolCallId == new ToolCallId("status-held-callback"));
                    using var status = JsonDocument.Parse(result.ToolResult.Content);
                    Assert.Equal(accepted.Run.RunId.Value, status.RootElement.GetProperty("run_id").GetString());
                    Assert.Equal("Completed", status.RootElement.GetProperty("state").GetString());
                }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            }
            else
            {
                var factory = _start!.ControlInvocation!.SpawnChildActor!;
                statusTask = factory(new ChildControlRequest(accepted.Run.RunId, false), "status-with-held-callback", _start.ControlToken);
                var status = Assert.IsType<ChildControlReply>(await statusTask.WaitAsync(Ceiling, TestContext.Current.CancellationToken));
                Assert.Equal(BackgroundChildState.Cancelled, status.Run!.State);
                Assert.NotNull(status.Run.DispatchClosedAtMs);
                Assert.Equal(checkpoint.Checkpoint, status.Run.ChildCheckpoint);
                Assert.False(provider.Pending.Task.IsCompleted);
            }
            Assert.False(callbackExited.Task.IsCompleted);
            Assert.Equal(2, provider.Count);
            Assert.Equal(1, Volatile.Read(ref effects));
        }
        finally
        {
            callbackRelease.Set();
            _childRelease.TrySetResult();
            provider.Pending.TrySetCanceled(TestContext.Current.CancellationToken);
            try
            {
                if (callbackEntered.Task.IsCompletedSuccessfully)
                    await callbackExited.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                if (provider.Held.Task.IsCompletedSuccessfully)
                    await provider.Exited.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                if (statusTask is not null)
                    await statusTask.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            }
            finally
            {
                controlRelease.TrySetResult();
                provider.Dispose();
            }
        }
    }
}
