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
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
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

    private static TurnContext InteractiveTurnContext(SessionId sessionId) => new()
    {
        SessionId = sessionId,
        TurnId = new TurnId("test-turn"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.SignalR,
        RequesterSenderId = new SenderId("local-user"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };

    [Fact]
    public async Task Approval_wait_does_not_consume_tool_execution_timeout_budget()
    {
        var executor = new ApprovalThenSuccessExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("tool-pipeline-probe");
        var approvalRequestTcs = new TaskCompletionSource<ToolInteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = new SessionId("D1/approval-timeout-test");

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
            sessionId,
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
            emitApprovalRequest: request => approvalRequestTcs.TrySetResult(request.Request),
            approvalTimeout: Timeout.InfiniteTimeSpan,
            turnContext: InteractiveTurnContext(sessionId),
            ct: TestContext.Current.CancellationToken);

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
    public async Task Source_less_approval_required_turn_fails_closed_without_prompt()
    {
        var executor = new ApprovalThenSuccessExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("source-less-approval-probe");
        var approvals = new List<ToolInteractionRequest>();

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-no-source", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "git push origin dev"
            })
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            new SessionId("D1/source-less-approval-test"),
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
            emitApprovalRequest: request => approvals.Add(request.Request),
            approvalTimeout: Timeout.InfiniteTimeSpan,
            ct: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Contains("no interactive approval requester is available", result.Content);
        Assert.Empty(approvals);
    }

    [Fact]
    public async Task Approve_once_does_not_reprompt_on_retry_execution()
    {
        var executor = new ApprovalThenSuccessExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("approve-once-no-reprompt-probe");
        var approvals = new List<ToolInteractionRequest>();
        var sessionId = new SessionId("signalr/approve-once-no-reprompt");

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
            sessionId,
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
            emitApprovalRequest: request => approvals.Add(request.Request),
            approvalTimeout: Timeout.InfiniteTimeSpan,
            turnContext: InteractiveTurnContext(sessionId),
            ct: TestContext.Current.CancellationToken);

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
        var sessionId = new SessionId("D1/cwd-propagation-test");

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
            sessionId,
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
            emitApprovalRequest: request => approvalRequestTcs.TrySetResult(request.Request),
            approvalTimeout: Timeout.InfiniteTimeSpan,
            turnContext: InteractiveTurnContext(sessionId),
            ct: TestContext.Current.CancellationToken);

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
    public async Task Approval_wait_is_cancelled_by_tool_execution_token()
    {
        var executor = new ApprovalThenSuccessExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("approval-cancel-probe");
        var approvalRequestTcs = new TaskCompletionSource<ToolInteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = new SessionId("D1/approval-cancel-test");
        using var executionCts = new CancellationTokenSource();

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-cancel", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "git push origin dev"
            })
        };

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            toolCalls,
            sessionId,
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
            emitApprovalRequest: request => approvalRequestTcs.TrySetResult(request.Request),
            approvalTimeout: Timeout.InfiniteTimeSpan,
            turnContext: InteractiveTurnContext(sessionId),
            ct: executionCts.Token);

        var approvalRequest = await approvalRequestTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        await executionCts.CancelAsync();

        var failed = await probe.ExpectMsgAsync<ToolExecutionFailed>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<TimeoutException>(failed.Cause);
        Assert.False(approvalChannel.Complete(approvalRequest.CallId, ApprovalDecision.ApprovedOnce));
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
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            ct: TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Tool_model_input_file_is_materialized_as_session_media_reference()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, FakePngBytes, TestContext.Current.CancellationToken);
        var executor = new ModelInputFileExecutor(imagePath);
        var probe = CreateTestProbe("model-input-file-probe");

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
            new SessionId("D1/model-input-file-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: dir.Path,
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(3),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            modelInputModalities: ModelModality.Text | ModelModality.Image,
            ct: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var mediaRef = Assert.Single(completed.ModelInputMediaReferences);
        Assert.Equal("image/png", mediaRef.MimeType.Value);
        Assert.Equal((int)MediaModality.Image, mediaRef.Modality);
        Assert.True(File.Exists(Path.Combine(dir.Path, SessionDirectoryHelper.MediaSubdirectory, mediaRef.RelativePath)));
    }

    [Fact]
    public async Task Streaming_tool_result_persists_model_input_media_references_on_tool_message()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, FakePngBytes, TestContext.Current.CancellationToken);
        var executor = new ModelInputFileExecutor(imagePath);
        var probe = CreateTestProbe("streaming-model-input-probe");

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
            new SessionId("D1/streaming-model-input-file-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: dir.Path,
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(3),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            streamToolResults: true,
            modelInputModalities: ModelModality.Text | ModelModality.Image,
            ct: TestContext.Current.CancellationToken);

        var single = await probe.ExpectMsgAsync<ToolExecutionSingleCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await probe.ExpectMsgAsync<ToolExecutionBatchCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var mediaRef = Assert.Single(single.Result.ModelInputMediaReferences);
        var persistedMediaRef = Assert.Single(single.Result.Message.MediaReferences);
        Assert.Equal(mediaRef.RelativePath, persistedMediaRef.RelativePath);
        Assert.Equal("image/png", persistedMediaRef.MimeType.Value);
    }

    [Fact]
    public async Task Tool_model_input_batch_limit_is_enforced_across_registered_files()
    {
        using var dir = new DisposableTempDir();
        var firstImagePath = Path.Combine(dir.Path, "first.png");
        var secondImagePath = Path.Combine(dir.Path, "second.png");
        await File.WriteAllBytesAsync(firstImagePath, FakePngBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(secondImagePath, FakePngBytes, TestContext.Current.CancellationToken);
        var context = new ToolExecutionContext("D1/model-input-budget-test", dir.Path)
        {
            Audience = TrustAudience.Personal,
            ModelInputModalities = ModelModality.Text | ModelModality.Image
        };
        context.AddModelInputFile(firstImagePath, "first.png", "image/png");
        context.AddModelInputFile(secondImagePath, "second.png", "image/png");
        var budget = new ModelInputBatchBudget(FakePngBytes.Length);

        var result = SessionToolExecutionPipeline.MaterializeModelInputFiles(
            context,
            dir.Path,
            logger: null,
            batchBudget: budget);

        Assert.Equal(2, result.RequestedCount);
        Assert.Single(result.MediaReferences);
    }

    [Fact]
    public async Task Tool_model_input_file_without_matching_modality_is_skipped()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, FakePngBytes, TestContext.Current.CancellationToken);
        var executor = new ModelInputFileExecutor(imagePath);
        var probe = CreateTestProbe("model-input-modality-probe");

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
            new SessionId("D1/model-input-modality-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: dir.Path,
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(3),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            ct: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Empty(completed.ModelInputMediaReferences);
    }

    [Fact]
    public async Task Tool_model_input_file_with_mismatched_magic_is_skipped()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, FakePdfBytes, TestContext.Current.CancellationToken);
        var executor = new ModelInputFileExecutor(imagePath);
        var probe = CreateTestProbe("model-input-magic-probe");

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
            new SessionId("D1/model-input-magic-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: dir.Path,
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(3),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            modelInputModalities: ModelModality.Text | ModelModality.Image,
            ct: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Empty(completed.ModelInputMediaReferences);
    }

    [Fact]
    public async Task Tool_model_input_file_over_size_limit_is_skipped()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "large.png");
        await using (var stream = File.Create(imagePath))
        {
            stream.SetLength(ChannelAttachmentPolicy.DefaultMaxFileBytes + 1);
        }
        var executor = new ModelInputFileExecutor(imagePath);
        var probe = CreateTestProbe("model-input-size-probe");

        var pipelineTask = SessionToolExecutionPipeline.ExecuteToolsAsync(
            executor,
            [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
            new SessionId("D1/model-input-size-test"),
            source: null,
            auditLogger: null,
            timeProvider: TimeProvider.System,
            sessionDir: dir.Path,
            maxInlineToolResultChars: 4096,
            timeout: TimeSpan.FromSeconds(3),
            self: probe.Ref,
            emitSubAgentOutput: _ => { },
            spawnChildActor: static (_, _, _) => Task.FromResult<object>(new object()),
            modelInputModalities: ModelModality.Text | ModelModality.Image,
            ct: TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Empty(completed.ModelInputMediaReferences);
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

    private sealed class ModelInputFileExecutor(string imagePath, string mimeType = "image/png") : IToolExecutor
    {
        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            context?.AddModelInputFile(imagePath, "diagram.png", mimeType);
            return Task.FromResult("image loaded");
        }
    }

    private static readonly byte[] FakePngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    private static readonly byte[] FakePdfBytes = "%PDF-1.7\nfake body\n%%EOF"u8.ToArray();

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
