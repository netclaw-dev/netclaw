// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.StartLifetime.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
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
    [Fact]
    public async Task Concurrent_equivalent_starts_recover_a_lost_actual_acceptance_reply_without_a_second_child()
    {
        using var lostReply = new CancellationTokenSource();
        _start!.StartReplyWaitCancellation = lostReply.Token;
        var startedEntered = NewSignal();
        var startedRelease = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is ChildRunEvent.Started)
            {
                startedEntered.TrySetResult();
                await startedRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return false;
        });
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await SendOriginalAsync(manager);
            await startedEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var before = await ReadJournalAsync();
            var accepted = Assert.Single(before.OfType<ChildRunAccepted>());
            Assert.Empty(before.OfType<ChildRunEvent.Started>());
            Assert.Equal(0, _child.CallCount);
            lostReply.Cancel();
            await _start.StartReplyWaitCancelled.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var retry = Retry(accepted);
            var first = CreateTestProbe();
            var second = CreateTestProbe();
            owner.Tell(retry, first.Ref);
            owner.Tell(retry, second.Ref);
            startedRelease.TrySetResult();
            var firstReply = await first.ExpectMsgAsync<ChildStartReply.Accepted>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var secondReply = await second.ExpectMsgAsync<ChildStartReply.Accepted>(Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            foreach (var reply in new[] { firstReply, secondReply })
            {
                Assert.Equal(accepted.Run.RunId, reply.Run.RunId);
                Assert.Equal(accepted.Run.StartKey, reply.Run.StartKey);
                Assert.Equal(accepted.Run.ArgumentsDigest, reply.Run.ArgumentsDigest);
                Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, reply.Run.OriginalContext));
                Assert.Equal(BackgroundChildState.Running, reply.State);
            }
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            var after = await ReadJournalAsync();
            Assert.Single(after.OfType<ChildRunAccepted>());
            Assert.Single(after.OfType<ChildRunEvent.Started>());
            Assert.Empty(after.OfType<ChildRunEvent.TerminalRecorded>());
            var result = Assert.Single(after.OfType<ToolCallRecorded>());
            Assert.Contains("Error executing tool:", result.ToolResult.Content, StringComparison.Ordinal);
            Assert.NotNull(result.LoopObservation);
            Assert.NotEqual((int)ToolInvocationOutcomeCategory.Success, result.LoopObservation.Category);
            Assert.False(result.LoopObservation.MissingReceipt);
            Assert.Equal(1, _child.CallCount);
            Assert.False(_childRelease.Task.IsCompleted);
            var child = await Sys.ActorSelection($"{owner.Path}/child-run-{accepted.Run.RunId.Value}")
                .Ask<ActorIdentity>(new Identify("single-child-after-concurrent-retry"), Ceiling, TestContext.Current.CancellationToken);
            Assert.NotNull(child.Subject);
            var recorded = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(retry, Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(accepted.Run.RunId, recorded.Run.RunId);
            Assert.Equal(after.Length, (await ReadJournalAsync()).Length);
        }
        finally { startedRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Cancellation_of_the_original_start_invocation_does_not_cancel_the_accepted_child_or_its_controls()
    {
        using var invocationCancellation = new CancellationTokenSource();
        _start!.StartInvocationCancellation = invocationCancellation.Token;
        var entered = NewSignal();
        var release = NewSignal();
        var nextProvider = NewSignal();
        var calls = 0;
        Host.Services.GetRequiredService<ToolRegistry>().ReplaceCore(new FakeNetclawTool(
            "file_read", "The child continued after its original dispatch ended.", "file", invocation =>
            {
                Interlocked.Increment(ref calls);
                invocation.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
            }));
        _child.ToolCallsOnFirstCall = [ReadCall("after-start-dispatch")];
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is ChildRunEvent.Started)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return false;
        });
        var (_, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Delegate the neutral task.", Source = DeliverySource("original", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var accepted = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
            var startToken = _start!.Token;
            Assert.True(startToken.CanBeCanceled);
            Assert.False(startToken.IsCancellationRequested);
            Assert.Equal(invocationCancellation.Token, startToken);
            Assert.Equal(startToken, _start.Prepared!.Execution.Cancellation);
            Assert.False(_start.StartReplyWaitCancelled.Task.IsCompleted);
            Assert.False(_child.FirstCallEntered.Task.IsCompleted);
            await invocationCancellation.CancelAsync();
            await _start.StartReplyWaitCancelled.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.True(startToken.IsCancellationRequested);
            Assert.False(_child.FirstCallEntered.Task.IsCompleted);
            release.TrySetResult();
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            var startRecords = await ReadJournalAsync();
            Assert.Single(startRecords.OfType<ChildRunAccepted>());
            Assert.Single(startRecords.OfType<ChildRunEvent.Started>());
            Assert.Empty(startRecords.OfType<ChildRunEvent.TerminalRecorded>());
            var startResult = Assert.Single(startRecords.OfType<ToolCallRecorded>());
            Assert.Contains("Error executing tool:", startResult.ToolResult.Content, StringComparison.Ordinal);
            Assert.NotNull(startResult.LoopObservation);
            Assert.NotEqual((int)ToolInvocationOutcomeCategory.Success, startResult.LoopObservation.Category);
            Assert.False(startResult.LoopObservation.MissingReceipt);
            Assert.False(_childRelease.Task.IsCompleted);
            _main.PlannedResponses.Enqueue([new FunctionCallContent("independent-status", "status_json_probe",
                new Dictionary<string, object?>
                {
                    ["run_id"] = accepted.Run.RunId.Value, ["cancel"] = false,
                    ["_rationale"] = "Inspect the child after the original invocation is cancelled."
                })]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Inspect the accepted child.", Source = DeliverySource("status", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            var status = Assert.IsType<ToolResultOutput>(await subscriber.FishForMessageAsync<object>(
                message => message is ToolResultOutput, Ceiling, cancellationToken: TestContext.Current.CancellationToken));
            await CompletedAsync(subscriber);
            using (var body = JsonDocument.Parse(status.Result))
            {
                Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
                Assert.Equal("Running", body.RootElement.GetProperty("state").GetString());
                Assert.False(body.RootElement.GetProperty("cancellation_requested").GetBoolean());
            }
            _child.NextResponseGate = nextProvider;
            _childRelease.TrySetResult();
            await AwaitAssertAsync(() => Assert.Equal(2, _child.CallCount), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var continued = await ReadJournalAsync();
            var checkpoint = Assert.Single(continued.OfType<ChildRunEvent.Checkpointed>());
            Assert.Equal(accepted.Run.RunId, checkpoint.RunId);
            Assert.Equal("The child continued after its original dispatch ended.", checkpoint.Checkpoint.Summary);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(nextProvider.Task.IsCompleted);
            Assert.Empty(continued.OfType<ChildRunEvent.CancellationRequested>());
            _main.PlannedResponses.Enqueue([ControlCall("independent-cancel", accepted.Run.RunId)]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Cancel the accepted child.", Source = DeliverySource("cancel", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            var control = await _start.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(BackgroundChildState.Cancelling, control.Run!.State);
            await AwaitAssertAsync(async () =>
            {
                var final = await ReadJournalAsync();
                Assert.Single(final.OfType<ChildRunAccepted>());
                Assert.Single(final.OfType<ChildRunEvent.Started>());
                var terminal = Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
                Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, _child.CallCount);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.TrySetResult();
            nextProvider.TrySetResult();
        }
    }

    [Fact]
    public async Task A_child_provider_that_ignores_cancellation_cannot_delay_owner_terminal_settlement()
    {
        var provider = new UncooperativeChildClient(_child);
        Assert.IsType<RoleProvider>(Host.Services.GetRequiredService<IChatClientProvider>()).Child = provider;
        var path = Path.Combine(_directory!.Path, "confirmed-before-uncooperative-provider.txt");
        var calls = 0;
        Host.Services.GetRequiredService<ToolRegistry>().ReplaceCore(new FakeNetclawTool(
            "file_read", "The confirmed checkpoint precedes the held remote task.", "file", invocation =>
            {
                Interlocked.Increment(ref calls);
                invocation.TryComplete(new ToolInvocationReceipt.Succeeded([new ToolFileActivity(path, ToolFileActivityKind.Read)], null));
            }));
        _child.ToolCallsOnFirstCall = [ReadCall("before-uncooperative-provider")];
        var (_, manager, subscriber) = await CreateOwnerAsync();
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
            var pair = Assert.Single(provider.HeldMessages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
            Assert.Equal("before-uncooperative-provider", pair.CallId);
            Assert.Equal(checkpoint.Checkpoint.Summary, Assert.IsType<string>(pair.Result));
            Assert.Equal(2, provider.Count);
            Assert.False(provider.Pending.Task.IsCompleted);
            _main.PlannedResponses.Enqueue([ControlCall("cancel-uncooperative-provider", accepted.Run.RunId)]);
            await manager.Ask<CommandAck>(new SendUserMessage
            { SessionId = Session, Content = "Cancel the accepted child.", Source = DeliverySource("cancel", "operator-a") },
                Ceiling, TestContext.Current.CancellationToken);
            var control = await _start!.ControlReply.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.Equal(BackgroundChildState.Cancelling, control.Run!.State);
            ChildRunEvent.TerminalRecorded? terminal = null;
            await AwaitAssertAsync(async () =>
            {
                var final = await ReadJournalAsync();
                Assert.Single(final.OfType<ChildRunEvent.CancellationRequested>());
                Assert.Single(final.OfType<ChildRunEvent.DispatchClosed>());
                terminal = Assert.Single(final.OfType<ChildRunEvent.TerminalRecorded>());
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(terminal);
            Assert.False(terminal.Terminal.Result.Success);
            Assert.Equal(SubAgentRunOutcome.Failed, terminal.Terminal.Result.Outcome);
            Assert.Equal(SubAgentOutcomeReason.CancelledByParent, terminal.Terminal.Result.OutcomeReason);
            var completion = Assert.IsType<ChildRunCompletion.Cancelled>(terminal.Terminal.Result.Completion);
            Assert.Equal(path, Assert.Single(completion.ConfirmedActivity!.ReadFiles));
            Assert.True(provider.Token.IsCancellationRequested);
            Assert.False(provider.Pending.Task.IsCompleted);
            Assert.Equal(2, provider.Count);
            Assert.Equal(1, Volatile.Read(ref calls));
            var storage = Assert.IsType<ToolSessionScope.Bound>(_start.Prepared!.Execution.Scope.Authority.Session).Storage;
            var report = Path.Combine(storage.ArtifactDirectory.Value, "cancelled-results.json");
            using var body = JsonDocument.Parse(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
            Assert.Equal(accepted.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(checkpoint.Checkpoint.Summary, body.RootElement.GetProperty("summary").GetString());
            Assert.Equal("Cancelled", body.RootElement.GetProperty("state").GetString());
            Assert.Null(terminal.Terminal.EvidenceWarning);
            Assert.False(provider.Pending.Task.IsCompleted);
        }
        finally
        {
            _childRelease.TrySetResult();
            provider.Pending.TrySetCanceled(TestContext.Current.CancellationToken);
            if (provider.Held.Task.IsCompletedSuccessfully)
                await provider.Exited.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        }
    }

    private sealed class UncooperativeChildClient(IChatClient first) : IChatClient
    {
        private int _count;
        private CancellationTokenRegistration _registration;
        public Action? CancellationCallback { get; init; }
        public int Count => Volatile.Read(ref _count);
        public TaskCompletionSource Held { get; } = NewSignal();
        public TaskCompletionSource Exited { get; } = NewSignal();
        public TaskCompletionSource<ChatResponse> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public IReadOnlyList<ChatMessage> HeldMessages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 1)
                return first.GetResponseAsync(messages, options, cancellationToken);
            Token = cancellationToken;
            HeldMessages = messages.ToArray();
            if (CancellationCallback is { } callback)
                _registration = cancellationToken.Register(callback);
            Held.TrySetResult();
            return Pending.Task;
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => StreamAsync(messages, options, cancellationToken);
        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                var response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (var update in response.ToChatResponseUpdates()) yield return update;
            }
            finally { if (Count > 1) Exited.TrySetResult(); }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => first.GetService(serviceType, serviceKey);
        public void Dispose()
        {
            _registration.Dispose();
            first.Dispose();
        }
    }
}
