// -----------------------------------------------------------------------
// <copyright file="ChildNoticeToolBatchCrashSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// PR 2515 review, Should-fix 1. A notice is admitted while a tool call of the parent runs. The actor crashes before
/// the tool batch ends. The journal then holds an assistant <c>tool_use</c> with no <c>tool_result</c>, and the notice
/// is an orphan input. A provider refuses a request whose history holds a <c>tool_use</c> with no result. The recovery
/// must close the interrupted batch before the notice turn sends a model request.
/// </summary>
public sealed class ChildNoticeToolBatchCrashSpecs(ITestOutputHelper output) : ChildNoticeTestBase(output)
{
    private const string HoldCallId = "hold-x";

    private readonly HoldTool _hold = new();

    protected override void RegisterParentTools(ToolRegistry registry) => registry.RegisterCore(_hold);

    /// <summary>
    /// Alice's turn starts a long tool call. Her child ends, and its notice is admitted during the tool call. The actor
    /// stops with no drain, and then it recovers. The recovered session must not send a model request that holds the
    /// interrupted <c>tool_use</c> with no result. The notice must still run in its own turn, and the parent reads the
    /// result file and replies.
    /// Failure on 268ffc1de: <c>ResumeOrphanNotices</c> calls <c>StartWaitingInputAfterApproval</c>, which calls
    /// <c>FireLlmCall</c> over the interrupted batch, because the batch has no parked approval.
    /// </summary>
    [Fact]
    public async Task A_notice_admitted_during_a_tool_call_does_not_send_a_request_over_the_interrupted_batch_after_a_crash()
    {
        // Arrange
        var sessionId = new SessionId("notice/crash-in-tool-batch");
        var journal = await JournalAsync();
        Clients.Parent.ResponseContentsSelector = request =>
            FreshUserTexts(request).Any(text => text.StartsWith("hold:", StringComparison.Ordinal))
                ? [Hold(HoldCallId)]
                : ScriptedParentReply(request);
        var subscriber = await JoinAsync(sessionId);
        var (accepted, childGate) = await StartChildAsync(sessionId, Alice, "one");
        await ExpectTurnsAsync(subscriber, 1);

        try
        {
            await SendAsync(sessionId, "hold:x", Operator(Alice));
            await _hold.Entered.Task.WaitAsync(Deadline, Cancellation);
            childGate.TrySetResult();
            await AwaitAssertAsync(() => Assert.Single(NoticesFor(journal, accepted.Run.RunId)), Deadline,
                cancellationToken: Cancellation);
            var notice = Assert.Single(NoticesFor(journal, accepted.Run.RunId));
            Assert.Equal(0, ConsumptionCount(journal, notice.InputId));
            Assert.Contains(journal.Committed.OfType<ToolBatchStarted>(),
                batch => batch.AssistantMessage.ToolCalls.Any(call => call.CallId.Value == HoldCallId));
            Assert.Empty(journal.Committed.OfType<ToolBatchAbandoned>());

            // A crash: the actor stops while the tool call runs. No graceful drain runs.
            await StopAsync(await ResolveSessionAsync(sessionId));
            _hold.Release.TrySetResult();
            var requestsBeforeRecovery = Clients.Parent.ReceivedMessages.Count;

            // Act: the recovery runs on the next join.
            var recoveredSubscriber = await JoinAsync(sessionId);

            // Assert: the notice runs once, in a turn that completes.
            await AwaitAssertAsync(() => Assert.True(ConsumptionCount(journal, notice.InputId) > 0,
                "The admitted notice never ran after the crash recovery."), Deadline, cancellationToken: Cancellation);
            await ExpectTurnsAsync(recoveredSubscriber, 1);
            await AwaitAssertAsync(async () => Assert.True(await IsIdleAsync(sessionId),
                "The recovered notice turn did not end."), Deadline, cancellationToken: Cancellation);

            // Assert: no model request after the recovery holds a tool_use with no tool_result.
            var recovered = Clients.Parent.ReceivedMessages.Skip(requestsBeforeRecovery).ToArray();
            Assert.NotEmpty(recovered);
            for (var index = 0; index < recovered.Length; index++)
            {
                var orphans = UnansweredCallIds(recovered[index]);
                Assert.True(orphans.Count == 0,
                    $"Model request {index + 1} after the recovery holds a tool_use with no tool_result "
                    + $"({string.Join(", ", orphans)}). A provider refuses this request.");
            }

            // Assert: the notice was not closed unread. The parent read result.md and replied.
            Assert.Equal(1, ConsumptionCount(journal, notice.InputId));
            Assert.DoesNotContain(journal.Committed.OfType<InputClosed>(), closed => closed.InputIds.Contains(notice.InputId));
            Assert.Single(NoticesFor(journal, accepted.Run.RunId));
            Assert.True(IndexOf(recovered, request => CarriesNoticeFor(request, accepted.Run.RunId)) >= 0,
                "No model request after the recovery carries the notice.");
            Assert.True(recovered.Any(request => Results(request).Any(result =>
                    result.Result?.ToString()?.Contains(ChildOutput, StringComparison.Ordinal) == true)),
                "The parent did not read the result file of the child after the recovery.");
            Assert.Equal(1, _hold.Runs);
        }
        finally
        {
            childGate.TrySetResult();
            _hold.Release.TrySetResult();
        }
    }

    // The call ids of a request that have a tool_use and no tool_result.
    private static IReadOnlyList<string> UnansweredCallIds(IReadOnlyList<ChatMessage> request)
    {
        var answered = Results(request).Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        return Calls(request).Select(call => call.CallId).Where(id => !answered.Contains(id)).ToArray();
    }

    private static FunctionCallContent Hold(string callId) => new(callId, HoldTool.ToolName,
        new Dictionary<string, object?> { ["_rationale"] = "Run a long command." });

    /// <summary>A tool that needs no approval and holds its call open until the spec releases it.</summary>
    private sealed class HoldTool : INetclawTool
    {
        public const string ToolName = "hold_probe";
        private int _runs;

        public TaskCompletionSource Entered { get; } = NewGate();
        public TaskCompletionSource Release { get; } = NewGate();
        public int Runs => Volatile.Read(ref _runs);

        public string Name => ToolName;
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Run a long command.";
        public string GrantCategory => "builtin";
        public JsonElement ParameterSchema => default;
        public AITool ToAITool() => AIFunctionFactory.Create(() => "held", Name, Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("This test requires invocation authority.");

        public async Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            Entered.TrySetResult();
            await Release.Task;
            return "held";
        }
    }
}
