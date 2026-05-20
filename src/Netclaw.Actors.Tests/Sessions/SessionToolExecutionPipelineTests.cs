// -----------------------------------------------------------------------
// <copyright file="SessionToolExecutionPipelineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionToolExecutionPipelineTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Approval_wait_does_not_consume_tool_execution_timeout_budget()
    {
        var executor = new ApprovalThenSuccessExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("tool-pipeline-probe");
        var approvalRequestTcs = new TaskCompletionSource<ToolInteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-1", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "git push origin dev"
            })
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            new SessionId("D1/approval-timeout-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: Path.GetTempPath(),
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(1),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            approvalChannel: approvalChannel,
            emitApprovalRequest: request => approvalRequestTcs.TrySetResult(request),
            approvalTimeout: Timeout.InfiniteTimeSpan);

        var approvalRequest = await approvalRequestTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        await probe.ExpectNoMsgAsync(
            TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken);

        approvalChannel.Complete(approvalRequest.CallId, ApprovalDecision.ApprovedOnce);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Single(completed.ToolResults);
        Assert.Equal("approved-and-ran", completed.ToolResults[0].Content);
    }

    [Fact]
    public async Task Approve_once_does_not_reprompt_on_retry_execution()
    {
        var executor = new ApprovalThenSuccessExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("approve-once-no-reprompt-probe");
        var approvals = new List<ToolInteractionRequest>();

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-approve-once", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "echo once"
            })
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            new SessionId("signalr/approve-once-no-reprompt"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: Path.GetTempPath(),
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(5),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            approvalChannel: approvalChannel,
            emitApprovalRequest: request => approvals.Add(request),
            approvalTimeout: Timeout.InfiniteTimeSpan);

        await AwaitAssertAsync(() =>
        {
            var firstRequest = Assert.Single(approvals);
            approvalChannel.Complete(firstRequest.CallId, ApprovalDecision.ApprovedOnce);
        }, duration: TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Single(completed.ToolResults);
        Assert.Equal("approved-and-ran", completed.ToolResults[0].Content);
        Assert.Single(approvals);
    }

    [Fact]
    public async Task Approval_request_propagates_cwd_from_approval_context()
    {
        // Regression: ToolApprovalContext.Cwd was never threaded into the
        // emitted ToolInteractionRequest, so PendingToolInteraction.Cwd ended
        // up null on the session-actor side. That silently turned every
        // "Always here" click (ApprovedAlways) into a global wildcard
        // because the persistence path read pending.Cwd to scope the entry.
        const string cwd = "/tmp/scoped-approval";
        var executor = new ApprovalWithCwdExecutor(cwd);
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("cwd-propagation-probe");
        var approvalRequestTcs = new TaskCompletionSource<ToolInteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-cwd", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "ls"
            })
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            new SessionId("D1/cwd-propagation-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: Path.GetTempPath(),
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(5),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            approvalChannel: approvalChannel,
            emitApprovalRequest: request => approvalRequestTcs.TrySetResult(request),
            approvalTimeout: Timeout.InfiniteTimeSpan);

        var approvalRequest = await approvalRequestTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(cwd, approvalRequest.Cwd);

        approvalChannel.Complete(approvalRequest.CallId, ApprovalDecision.ApprovedAlways);
        await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_stalled_tool_call_times_out_without_failing_its_healthy_sibling()
    {
        var executor = new ParallelStreamingExecutor();
        var probe = CreateTestProbe("parallel-tool-probe");

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-fast", "fast_tool", new Dictionary<string, object?>()),
            new("call-slow", "slow_tool", new Dictionary<string, object?>())
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            new SessionId("D1/parallel-watchdog-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: Path.GetTempPath(),
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(1),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()));

        // Real-time: the slow tool's per-call watchdog trips ~1-2s in (1s budget
        // plus the 1s poll interval). The ceiling stays tight so a regression —
        // a watchdog that never fires — surfaces fast rather than hanging.
        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(8),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Each call has its own watchdog: the stalled one is timed out
        // independently, the healthy one returns, and the batch is not failed
        // wholesale — both produce a tool-result message.
        Assert.Equal(2, completed.ToolResults.Count);
        var fast = completed.ToolResults.Single(r => r.Name == "fast_tool");
        var slow = completed.ToolResults.Single(r => r.Name == "slow_tool");
        Assert.Equal("fast_tool-ok", fast.Content);
        Assert.Contains("slow_tool", slow.Content);
        Assert.Contains("no activity", slow.Content);
    }

    /// <summary>
    /// Streaming executor: <c>slow_tool</c> never produces an item (its per-call
    /// watchdog must time it out); every other tool completes immediately.
    /// </summary>
    private sealed class ParallelStreamingExecutor : IToolExecutor
    {
        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => throw new NotSupportedException("ParallelStreamingExecutor is streaming-only.");

        public async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (toolCall.Name == "slow_tool")
            {
                // Never produces an item — the per-call watchdog must time it out.
                await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
            }

            await Task.Yield();
            yield return new ToolCompletedUpdate($"{toolCall.Name}-ok");
        }
    }

    private sealed class ApprovalThenSuccessExecutor : IToolExecutor
    {
        private int _attempt;

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => ExecuteAsync(toolCall, context, ct);

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            _attempt++;

            if (_attempt == 1)
            {
                throw new ToolApprovalRequiredException(new ToolApprovalContext(
                    ToolName: toolCall.Name,
                    DisplayText: "git push origin dev",
                    Patterns: ["git push origin dev"],
                    CandidateVerbs: ["git push origin dev"],
                    Options:
                    [
                        new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                        new ToolApprovalOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                        new ToolApprovalOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                        new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                    ]));
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult("approved-and-ran");
        }
    }

    private sealed class ApprovalWithCwdExecutor(string cwd) : IToolExecutor
    {
        private int _attempt;

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => ExecuteAsync(toolCall, context, ct);

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            _attempt++;
            if (_attempt == 1)
            {
                throw new ToolApprovalRequiredException(new ToolApprovalContext(
                    ToolName: toolCall.Name,
                    DisplayText: "ls",
                    Patterns: ["ls"],
                    CandidateVerbs: ["ls"],
                    Options:
                    [
                        new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                        new ToolApprovalOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                        new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                    ],
                    Cwd: cwd));
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult("ok");
        }
    }
}
