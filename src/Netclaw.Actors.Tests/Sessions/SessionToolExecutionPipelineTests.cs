// -----------------------------------------------------------------------
// <copyright file="SessionToolExecutionPipelineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tests.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

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
    public void Batch_derives_tool_authority_from_admitted_turn()
    {
        var turnContext = InteractiveTurnContext(new SessionId("D1/admitted-session")) with
        {
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                "signalr", "session", "default-target", "Default"),
            RequestedDeliveryTarget = new ChannelDeliveryTargetInfo(
                "signalr", "session", "requested-target", "Requested")
        };
        var batch = new SessionToolBatch(
            turnContext,
            new SessionToolRunEnvironment
            {
                SessionDirectory = Path.GetTempPath(),
                InlineOutputBudget = new InlineOutputBudget(4096),
                SpawnChildActor = static (_, _, _) => Task.FromResult<object>(new object())
            })
        {
            ToolCalls = [new FunctionCallContent("call-1", "inspect_context")],
            DefaultTimeout = new ToolExecutionTimeout(TimeSpan.FromSeconds(5)),
            ReplyTo = ActorRefs.Nobody,
            EmitSubAgentOutput = _ => { },
            ApprovalRequests = new ToolApprovalRequests(
                new ApprovalChannel(),
                _ => { },
                new ToolExecutionTimeout(Timeout.InfiniteTimeSpan)),
            BackgroundJobs = new BackgroundJobDispatch.Unavailable(),
            CancellationToken = TestContext.Current.CancellationToken
        };

        var session = Assert.IsType<ToolSessionScope.Bound>(batch.RunScope.Session);
        Assert.Equal(turnContext.SessionId.Value, session.SessionId);
        Assert.Equal(turnContext.Audience, batch.RunScope.Audience);
        Assert.Equal(turnContext.Boundary, batch.RunScope.Boundary);
        Assert.Equal(turnContext.ChannelType?.ToWireValue(), batch.RunScope.ChannelType);
        Assert.IsType<InteractiveApprovalCapability.Unavailable>(batch.RunScope.InteractiveApproval);
        Assert.Equal(turnContext.DefaultDeliveryTarget, batch.RunScope.DefaultDeliveryTarget);
        Assert.Equal(turnContext.RequestedDeliveryTarget, batch.RunScope.RequestedDeliveryTarget);
    }

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

        var pipelineTask = new SessionToolPipelineTestFixture(executor, toolCalls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .WithTimeout(TimeSpan.FromSeconds(1))
            .WithApprovals(
                approvalChannel,
                request => approvalRequestTcs.TrySetResult(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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
    public async Task Approved_always_seeds_the_immediate_retry_bypass_so_a_partially_covered_command_still_runs()
    {
        // Regression for the "approved command still throws" trigger
        // (https://github.com/netclaw-dev/netclaw/issues/1802): the pipeline seeded
        // the one-time retry bypass only for ApprovedOnce, so a command approved
        // with ApprovedSession/ApprovedAlways whose durable grant does not cover
        // every verb re-hit the gate on retry and failed the turn. This fake
        // re-requires approval until the immediate retry carries the bypass.
        var executor = new BypassRequiredOnRetryExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("approved-always-bypass-probe");
        var approvalRequestTcs = new TaskCompletionSource<ToolInteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = new SessionId("D1/approved-always-bypass");

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-1", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "gh api foo/bar 2>/dev/null | base64 -d | head"
            })
        };

        var pipelineTask = new SessionToolPipelineTestFixture(executor, toolCalls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .WithApprovals(
                approvalChannel,
                request => approvalRequestTcs.TrySetResult(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var approvalRequest = await approvalRequestTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        approvalChannel.Complete(approvalRequest.CallId, ApprovalDecision.ApprovedAlways);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Equal("ran-with-bypass", result.Content);
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

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("D1/source-less-approval-test"), probe.Ref)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .WithApprovals(
                approvalChannel,
                request => approvals.Add(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Contains("no interactive approval requester is available", result.Content);
        Assert.Empty(approvals);
    }

    [Fact]
    public async Task Undeclared_project_scope_returns_agent_correction_without_user_prompt()
    {
        var executor = new ProjectScopeDeclarationRequiredExecutor("/home/user/repos/project");
        var probe = CreateTestProbe("project-scope-correction-probe");
        var approvals = new List<ToolInteractionRequest>();
        var sessionId = new SessionId("D1/project-scope-correction");
        var toolCalls = new List<FunctionCallContent>
        {
            new("call-1", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "head -40 src/file.cs"
            })
        };

        var pipelineTask = new SessionToolPipelineTestFixture(executor, toolCalls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .WithSetWorkingDirectoryAvailable()
            .WithApprovals(
                new ApprovalChannel(),
                request => approvals.Add(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Equal(
            "Tool execution deferred: working_directory_not_declared\n" +
            "Project directory: '/home/user/repos/project'.\n" +
            "Next action: call set_working_directory with an allowed project directory for this task, then retry the failed tool call.",
            result.Content);
        Assert.Equal(
            ToolRemediationCode.SetWorkingDirectory,
            completed.ToolReceipts["call-1"].RemediationCode);
        Assert.Empty(approvals);
        Assert.Equal(1, executor.Attempts);
    }

    [Fact]
    public async Task Streaming_result_is_presented_before_delivery()
    {
        var executor = new CorrectiveReceiptExecutor();
        var probe = CreateTestProbe("streaming-remediation-probe");
        var call = new FunctionCallContent(
            "call-streaming-remediation",
            "file_read",
            new Dictionary<string, object?> { ["Path"] = "README.md" });

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [call],
                new SessionId("D1/streaming-remediation"),
                probe.Ref)
            .WithSetWorkingDirectoryAvailable()
            .StreamingResults()
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionSingleCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await probe.ExpectMsgAsync<ToolExecutionBatchCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(
            "Error: invalid_context: No project or session directory is available.\n" +
            "Next action: call set_working_directory with an allowed project directory for this task, then retry the failed tool call.",
            completed.Result.Message.Content);
        Assert.Equal(ToolRemediationCode.SetWorkingDirectory, completed.Result.Receipt?.RemediationCode);
    }

    [Fact]
    public async Task Hidden_working_directory_tool_is_not_named_by_parent_result()
    {
        var executor = new CorrectiveReceiptExecutor();
        var probe = CreateTestProbe("hidden-remediation-probe");
        var call = new FunctionCallContent(
            "call-hidden-remediation",
            "file_read",
            new Dictionary<string, object?> { ["Path"] = "README.md" });

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [call],
                new SessionId("D1/hidden-remediation"),
                probe.Ref)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Equal("Error: invalid_context: No project or session directory is available.", result.Content);
        Assert.DoesNotContain(SetWorkingDirectoryTool.ToolName, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_platform_temp_calls_all_return_corrections_before_prompt()
    {
        var executor = new ScratchCorrectionRequiredExecutor();
        var probe = CreateTestProbe("scratch-parallel-correction-probe");
        var approvals = new List<ToolInteractionRequest>();
        var sessionId = new SessionId("D1/scratch-parallel-correction");
        var calls = new List<FunctionCallContent>
        {
            ScratchCall("scratch-1"),
            ScratchCall("scratch-2")
        };

        var pipelineTask = new SessionToolPipelineTestFixture(executor, calls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .InSessionDirectory("/home/user/.netclaw/sessions/example")
            .WithApprovals(
                new ApprovalChannel(),
                request => approvals.Add(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(2, completed.ToolResults.Count);
        Assert.All(completed.ToolResults, result =>
            Assert.Equal(
                "Tool execution deferred: shared_temporary_directory\n" +
                "Session scratch directory: '/home/user/.netclaw/sessions/example'.\n" +
                "Next action: use the session scratch directory from this result for disposable files, or retry unchanged for exact platform paths.",
                result.Content));
        Assert.All(completed.ToolReceipts.Values, receipt =>
            Assert.Equal(ToolRemediationCode.UseSessionScratch, receipt.RemediationCode));
        Assert.Equal(2, completed.ScratchCorrectionChanges.Count);
        Assert.All(completed.ScratchCorrectionChanges,
            change => Assert.IsType<SessionScratchCorrectionChange.Arm>(change));
        Assert.Empty(approvals);
    }

    [Fact]
    public async Task Later_exact_retry_consumes_key_and_offers_once_or_deny()
    {
        var key = ScratchCorrectionRequiredExecutor.Key;
        var executor = new ScratchRetryApprovalExecutor();
        var approvalChannel = new ApprovalChannel();
        var probe = CreateTestProbe("scratch-retry-probe");
        var approvalRequest = new TaskCompletionSource<ToolInteractionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = new SessionId("D1/scratch-retry");

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [ScratchCall("scratch-retry")],
                sessionId,
                probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .InSessionDirectory(key.SessionDirectory)
            .WithScratchCorrections(key)
            .WithApprovals(
                approvalChannel,
                request => approvalRequest.TrySetResult(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var request = await approvalRequest.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        Assert.Equal([ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            request.Options.Select(option => option.Key.Value));
        approvalChannel.Complete(request.CallId, ApprovalDecision.Denied);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.True(executor.RetryMarked);
        var change = Assert.Single(completed.ScratchCorrectionChanges);
        Assert.Equal(key, Assert.IsType<SessionScratchCorrectionChange.Consume>(change).Key);
    }

    [Fact]
    public void Scratch_retry_key_is_consumed_once()
    {
        var key = ScratchCorrectionRequiredExecutor.Key;
        var dispatch = new SessionScratchCorrectionDispatch([key]);

        Assert.True(dispatch.TryConsume(key.Call, out var consumed));
        Assert.Equal(key, consumed);
        Assert.False(dispatch.TryConsume(key.Call, out _));
    }

    [Theory]
    [InlineData("different-command", true, "/tmp", false, 5)]
    [InlineData("gh api repos/example/project", false, null, false, 5)]
    [InlineData("gh api repos/example/project", true, "/var/tmp", false, 5)]
    [InlineData("gh api repos/example/project", true, "/tmp", true, 5)]
    [InlineData("gh api repos/example/project", true, "/tmp", false, 30)]
    public void Execution_change_does_not_consume_scratch_retry_key(
        string command,
        bool hasExplicitWorkingDirectory,
        string? workingDirectory,
        bool background,
        int timeoutSeconds)
    {
        var key = ScratchCorrectionRequiredExecutor.Key;
        var dispatch = new SessionScratchCorrectionDispatch([key]);
        var changedCall = key.Call with
        {
            Command = command,
            HasExplicitWorkingDirectory = hasExplicitWorkingDirectory,
            ExplicitWorkingDirectory = workingDirectory,
            Background = background,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        Assert.False(dispatch.TryConsume(changedCall, out _));
    }

    [Fact]
    public void Rationale_is_not_part_of_scratch_retry_semantics()
    {
        var call = ScratchCall("scratch-rationale");
        var first = SessionToolExecutionPipeline.BuildSessionScratchCallSemantics(
            call,
            new ToolCallMeta { Rationale = "first explanation" },
            TimeSpan.FromSeconds(5));
        var second = SessionToolExecutionPipeline.BuildSessionScratchCallSemantics(
            call,
            new ToolCallMeta { Rationale = "different explanation" },
            TimeSpan.FromSeconds(5));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Shell_change_does_not_consume_scratch_retry_key()
    {
        var key = ScratchCorrectionRequiredExecutor.Key;
        var dispatch = new SessionScratchCorrectionDispatch([key]);

        Assert.False(dispatch.TryConsume(
            key.Call with { Shell = ApprovalShell.PowerShell },
            out _));
    }

    [Fact]
    public void Scratch_correction_state_clears_lifecycle_authority()
    {
        var key = ScratchCorrectionRequiredExecutor.Key;
        var state = new SessionScratchCorrectionState();
        state.Apply(new SessionScratchCorrectionChange.Arm(key));
        state.Clear();

        Assert.False(state.Snapshot().TryConsume(key.Call, out _));
    }

    [Fact]
    public void Scratch_correction_state_removes_consumed_key_after_history_commit()
    {
        var key = ScratchCorrectionRequiredExecutor.Key;
        var state = new SessionScratchCorrectionState();
        state.Apply(new SessionScratchCorrectionChange.Arm(key));
        state.Apply(new SessionScratchCorrectionChange.Consume(key));

        Assert.False(state.Snapshot().TryConsume(key.Call, out _));
    }

    private static FunctionCallContent ScratchCall(string callId)
        => new(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = "gh api repos/example/project",
            ["WorkingDirectory"] = "/tmp"
        });

    [Theory]
    [InlineData(ChannelType.Headless)]
    [InlineData(ChannelType.Reminder)]
    [InlineData(ChannelType.Webhook)]
    public async Task Non_interactive_turn_does_not_create_subagent_approval_bridge(ChannelType channelType)
    {
        var executor = new ContextCapturingExecutor();
        var probe = CreateTestProbe($"non-interactive-{channelType}-context-probe");
        var sessionId = new SessionId($"automation/{channelType}");
        var source = new MessageSource
        {
            ChannelType = channelType,
            SenderId = new SenderId("automation"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
            ReceivedAt = DateTimeOffset.UnixEpoch
        };

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-1", "inspect_context")],
                sessionId,
                probe.Ref)
            .From(source)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .ExecuteAsync(TestContext.Current.CancellationToken);

        await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.NotNull(executor.Context);
        Assert.IsType<InteractiveApprovalCapability.Unavailable>(executor.Context.RunScope.InteractiveApproval);
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

        var pipelineTask = new SessionToolPipelineTestFixture(executor, toolCalls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .WithApprovals(
                approvalChannel,
                request => approvals.Add(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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

        var pipelineTask = new SessionToolPipelineTestFixture(executor, toolCalls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .WithApprovals(
                approvalChannel,
                request => approvalRequestTcs.TrySetResult(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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

        var pipelineTask = new SessionToolPipelineTestFixture(executor, toolCalls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .WithApprovals(
                approvalChannel,
                request => approvalRequestTcs.TrySetResult(request.Request),
                Timeout.InfiniteTimeSpan)
            .ExecuteAsync(executionCts.Token);

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

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("D1/parallel-watchdog-test"), probe.Ref)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .ExecuteAsync(TestContext.Current.CancellationToken);

        // Real-time: the slow tool's per-call budget token trips ~1s in (the 1s
        // wall-clock budget). The ceiling stays tight so a regression — a budget
        // that never fires — surfaces fast rather than hanging.
        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(8),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Each call has its own budget token: the stalled one is timed out
        // independently, the healthy one returns, and the batch is not failed
        // wholesale — both produce a tool-result message.
        Assert.Equal(2, completed.ToolResults.Count);
        var fast = completed.ToolResults.Single(r => r.Name == "fast_tool");
        var slow = completed.ToolResults.Single(r => r.Name == "slow_tool");
        Assert.Equal("fast_tool-ok", fast.Content);
        Assert.Contains("slow_tool", slow.Content);
        Assert.Contains("exceeded execution budget", slow.Content);
    }

    [Fact]
    public async Task Opaque_tool_stream_without_a_completion_item_surfaces_an_error()
    {
        var executor = new ParallelStreamingExecutor();
        var probe = CreateTestProbe("no-completion-probe");

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-nc", "no_completion_tool", new Dictionary<string, object?>())],
                new SessionId("D1/no-completion-test"),
                probe.Ref)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // A stream that ends without a completion item fails loudly as a per-tool
        // error result — not a hang, not a wholesale batch failure.
        var result = Assert.Single(completed.ToolResults);
        Assert.Equal("no_completion_tool", result.Name);
        Assert.Contains("without a completion item", result.Content);
    }

    [Fact]
    public async Task Opaque_streaming_output_does_not_extend_tool_wall_clock_budget()
    {
        var time = new FakeTimeProvider();
        var executor = new ChattyOpaqueExecutor();
        var probe = CreateTestProbe("opaque-wall-clock-probe");

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-chatty", "chatty_tool", new Dictionary<string, object?>())],
                new SessionId("D1/opaque-wall-clock-test"),
                probe.Ref)
            .WithTimeProvider(time)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .ExecuteAsync(TestContext.Current.CancellationToken);

        await executor.ActivitySeen.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(2));

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Equal("chatty_tool", result.Name);
        Assert.Contains("exceeded execution budget", result.Content);
    }

    [Fact]
    public async Task Self_monitoring_tool_runs_to_completion_without_a_parent_timeout()
    {
        // Self-monitoring tools are drained with NO parent watchdog — there is no
        // clock on this path at all. The run is bounded only by its own completion
        // (here) or caller cancellation (next test); the pipeline never times it out.
        var executor = new SelfMonitoringStreamingExecutor();
        var probe = CreateTestProbe("self-monitoring-probe");

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-self", "spawn_agent", new Dictionary<string, object?>())],
                new SessionId("D1/self-monitoring-test"),
                probe.Ref)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .ExecuteAsync(TestContext.Current.CancellationToken);

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        // Nothing has completed it and there is no timer to trip, so it stays running.
        Assert.False(pipelineTask.IsCompleted);

        executor.Complete("self-ok");

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var result = Assert.Single(completed.ToolResults);
        Assert.Equal("self-ok", result.Content);
    }

    [Fact]
    public async Task Self_monitoring_tool_is_bounded_only_by_caller_cancellation()
    {
        // A self-monitoring tool that never completes is ended ONLY by caller (turn/
        // user) cancellation — no parent watchdog exists. The cancel must surface as a
        // failed batch (ToolExecutionFailed), NOT as a tool-result error fed back to the
        // model as if the sub-agent had failed.
        var executor = new SelfMonitoringStreamingExecutor();
        var probe = CreateTestProbe("self-monitoring-cancel-probe");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-self", "spawn_agent", new Dictionary<string, object?>())],
                new SessionId("D1/self-monitoring-cancel-test"),
                probe.Ref)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .ExecuteAsync(cts.Token);

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(pipelineTask.IsCompleted); // never completes on its own

        cts.Cancel();

        var failed = await probe.ExpectMsgAsync<ToolExecutionFailed>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<TimeoutException>(failed.Cause);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tool_model_input_file_is_materialized_as_session_media_reference()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, FakePngBytes, TestContext.Current.CancellationToken);
        var executor = new ModelInputFileExecutor(imagePath);
        var probe = CreateTestProbe("model-input-file-probe");

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
                new SessionId("D1/model-input-file-test"),
                probe.Ref)
            .InSessionDirectory(dir.Path)
            .WithTimeout(TimeSpan.FromSeconds(3))
            .AcceptingModelInput(ModelModality.Text | ModelModality.Image)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
                new SessionId("D1/streaming-model-input-file-test"),
                probe.Ref)
            .InSessionDirectory(dir.Path)
            .WithTimeout(TimeSpan.FromSeconds(3))
            .StreamingResults()
            .AcceptingModelInput(ModelModality.Text | ModelModality.Image)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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
        var context = TestToolExecutionContext.CreateBound("D1/model-input-budget-test", dir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ModelInputModalities = ModelModality.Text | ModelModality.Image
        });
        context.AddModelInputFile(firstImagePath, "first.png", "image/png");
        context.AddModelInputFile(secondImagePath, "second.png", "image/png");
        var budget = new ModelInputBatchBudget(FakePngBytes.Length);

        var result = SessionToolExecutionPipeline.MaterializeModelInputFiles(
            context,
            dir.Path,
            NoLogger.Instance,
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

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
                new SessionId("D1/model-input-modality-test"),
                probe.Ref)
            .InSessionDirectory(dir.Path)
            .WithTimeout(TimeSpan.FromSeconds(3))
            .ExecuteAsync(TestContext.Current.CancellationToken);

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

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
                new SessionId("D1/model-input-magic-test"),
                probe.Ref)
            .InSessionDirectory(dir.Path)
            .WithTimeout(TimeSpan.FromSeconds(3))
            .AcceptingModelInput(ModelModality.Text | ModelModality.Image)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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

        var pipelineTask = new SessionToolPipelineTestFixture(
                executor,
                [new FunctionCallContent("call-image", FileReadTool.ToolName, new Dictionary<string, object?>())],
                new SessionId("D1/model-input-size-test"),
                probe.Ref)
            .InSessionDirectory(dir.Path)
            .WithTimeout(TimeSpan.FromSeconds(3))
            .AcceptingModelInput(ModelModality.Text | ModelModality.Image)
            .ExecuteAsync(TestContext.Current.CancellationToken);

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
                // Never produces an item — the per-call budget must time it out.
                await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
            }

            if (toolCall.Name == "no_completion_tool")
            {
                // Yields activity but no completion item — violates the tool-call
                // contract; the pipeline must surface a loud error, not hang.
                await Task.Yield();
                yield return new ToolActivityUpdate("working");
                yield break;
            }

            await Task.Yield();
            yield return new ToolCompletedUpdate($"{toolCall.Name}-ok");
        }
    }

    private sealed class ChattyOpaqueExecutor : IToolExecutor
    {
        public TaskCompletionSource ActivitySeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => throw new NotSupportedException("ChattyOpaqueExecutor is streaming-only.");

        public async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ActivitySeen.TrySetResult();
            yield return new ToolActivityUpdate("stdout", ".");
            await TestStreamingHelpers.ParkUntilCancelledAsync(ct);
        }
    }

    private sealed class SelfMonitoringStreamingExecutor : IToolExecutor
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolLivenessMode GetLivenessMode(FunctionCallContent toolCall) => ToolLivenessMode.SelfMonitoring;

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => throw new NotSupportedException("SelfMonitoringStreamingExecutor is streaming-only.");

        public void Complete(string result) => _completion.TrySetResult(result);

        public async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult();
            yield return new ToolActivityUpdate("calling the model");
            yield return new ToolCompletedUpdate(await _completion.Task.WaitAsync(ct));
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

    // Real PNG: the egress normalizer decodes every model-input image, so a
    // fake magic-byte stub would now be dropped. Small enough to pass through.
    private static readonly byte[] FakePngBytes = TestImages.SmallPng();

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

    private sealed class ProjectScopeDeclarationRequiredExecutor(string directory) : IToolExecutor
    {
        public int Attempts { get; private set; }

        public Task AuthorizeAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => ExecuteAsync(toolCall, context, ct);

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
        {
            Attempts++;
            throw new ToolApprovalRequiredException(new ToolApprovalContext(
                ToolName: toolCall.Name,
                DisplayText: "head -40 src/file.cs",
                Patterns: ["head"],
                CandidateVerbs: ["head"],
                Options: [])
            {
                SuggestedProjectDirectory = directory
            });
        }
    }

    private sealed class CorrectiveReceiptExecutor : IToolExecutor
    {
        public Task AuthorizeAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
        {
            var requiredContext = context
                ?? throw new InvalidOperationException("Execution context is required.");
            requiredContext.Outputs.TryComplete(new ToolInvocationReceipt(
                ToolInvocationOutcomeCategory.RecoverableCorrection,
                remediationCode: ToolRemediationCode.SetWorkingDirectory));
            return Task.FromResult("Error: invalid_context: No project or session directory is available.");
        }
    }

    private sealed class ScratchCorrectionRequiredExecutor : IToolExecutor
    {
        internal static SessionScratchCorrectionKey Key { get; } = new(
            new SessionScratchCallSemantics(
                ApprovalShell.Bash,
                "gh api repos/example/project",
                HasExplicitWorkingDirectory: true,
                ExplicitWorkingDirectory: "/tmp",
                Background: false,
                Timeout: TimeSpan.FromSeconds(5)),
            TemporaryRoot: "/tmp",
            SessionDirectory: "/home/user/.netclaw/sessions/example");

        public Task AuthorizeAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => ExecuteAsync(toolCall, context, ct);

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => throw new ToolApprovalRequiredException(new ToolApprovalContext(
                ToolName: toolCall.Name,
                DisplayText: Key.Call.Command,
                Patterns: ["gh api"],
                CandidateVerbs: ["gh api"],
                Options: [])
            {
                AgentCorrection = new ToolAgentCorrection.SessionScratchSuggested(
                    Key.SessionDirectory,
                    Key.TemporaryRoot,
                    Key.Call.Shell)
            });
    }

    private sealed class ScratchRetryApprovalExecutor
        : IToolExecutor, ISessionScratchRetryAwareExecutor
    {
        public ApprovalShell Shell => ApprovalShell.Bash;

        public bool RetryMarked { get; private set; }

        public void MarkSessionScratchRetry(
            ToolExecutionContext context,
            ToolAgentCorrection.SessionScratchSuggested correction)
            => RetryMarked = true;

        public Task AuthorizeAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => ExecuteAsync(toolCall, context, ct);

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
        {
            var options = RetryMarked
                ? new ToolApprovalOption[]
                {
                    new(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                }
                : [];
            throw new ToolApprovalRequiredException(new ToolApprovalContext(
                ToolName: toolCall.Name,
                DisplayText: "gh api repos/example/project",
                Patterns: ["gh api"],
                CandidateVerbs: ["gh api"],
                Options: options,
                Cwd: "/tmp")
            {
                IsSessionScratchRetry = RetryMarked,
                SessionScratchDirectory = ScratchCorrectionRequiredExecutor.Key.SessionDirectory,
                PlatformTemporaryRoot = ScratchCorrectionRequiredExecutor.Key.TemporaryRoot
            });
        }
    }

    private sealed class BypassRequiredOnRetryExecutor : IToolExecutor
    {
        private static readonly string[] Patterns = ["gh api", "base64", "head"];

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            // Stand in for the real gate on a command whose persisted grant does not
            // cover every candidate verb: authorization keeps requiring approval
            // until the immediate retry carries the one-time bypass for these
            // patterns. That bypass is what the pipeline must seed for every
            // approved scope, not just ApprovedOnce.
            var approval = context?.Approval;
            if (approval is not null
                && string.Equals(approval.OneTimeApprovedToolName, toolCall.Name, StringComparison.Ordinal)
                && Patterns.All(approval.OneTimeApprovedPatterns.Contains))
            {
                return Task.FromResult("ran-with-bypass");
            }

            throw new ToolApprovalRequiredException(new ToolApprovalContext(
                ToolName: toolCall.Name,
                DisplayText: "gh api foo/bar | base64 -d | head",
                Patterns: Patterns,
                CandidateVerbs: Patterns,
                Options:
                [
                    new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                    new ToolApprovalOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                    new ToolApprovalOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                    new ToolApprovalOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                ]));
        }
    }

    private sealed class ContextCapturingExecutor : IToolExecutor
    {
        public ToolExecutionContext? Context { get; private set; }

        public Task AuthorizeAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext? context = null,
            CancellationToken ct = default)
        {
            Context = context;
            return Task.FromResult("ok");
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
