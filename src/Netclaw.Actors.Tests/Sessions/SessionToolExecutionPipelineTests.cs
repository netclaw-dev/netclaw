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

    private sealed class ApprovalThenSuccessExecutor : IToolExecutor
    {
        private int _attempt;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            _attempt++;

            if (_attempt == 1)
            {
                throw new ToolApprovalRequiredException(new ToolApprovalContext(
                    ToolName: toolCall.Name,
                    DisplayText: "git push origin dev",
                    UnapprovedPatterns: ["git push"],
                    Options:
                    [
                        new ToolApprovalOption("approve_once", "Approve once"),
                        new ToolApprovalOption("approve_session", "Approve for this chat"),
                        new ToolApprovalOption("approve_always", "Approve always"),
                        new ToolApprovalOption("deny", "Deny")
                    ]));
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult("approved-and-ran");
        }
    }
}
