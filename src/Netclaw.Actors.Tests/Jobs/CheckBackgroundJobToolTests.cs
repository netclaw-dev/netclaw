// -----------------------------------------------------------------------
// <copyright file="CheckBackgroundJobToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Jobs;

public sealed class CheckBackgroundJobToolTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    private ToolExecutionContext MakeContext(string sessionId = "test/thread") => TestToolExecutionContext.CreateBound(sessionId, "/tmp", new TestToolExecutionContextOptions
        {
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal
    });

    [Fact]
    public async Task StatusQuery_ReturnsCorrectState()
    {
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeQueryManager(
            new BackgroundJobStatusResponse
            {
                JobId = new BackgroundJobId("abc123"),
                Status = BackgroundJobStatus.Running,
                Found = true,
                Elapsed = TimeSpan.FromSeconds(42),
                Rationale = "build test"
            })));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "abc123" };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("abc123", result);
        Assert.Contains("running", result);
        Assert.Contains("42.0s", result);
        Assert.Contains("build test", result);
    }

    [Fact]
    public async Task Cancel_SendsCancellationAndReturnsConfirmation()
    {
        var probe = CreateTestProbe("cancel-probe");
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeCancelManager(probe.Ref)));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "abc123", ["Cancel"] = true };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("Cancellation request sent", result);
        Assert.Contains("abc123", result);

        var cancelled = await probe.ExpectMsgAsync<CancelBackgroundJob>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("abc123", cancelled.JobId.Value);
    }

    [Fact]
    public async Task NonExistentJob_ReturnsError()
    {
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeQueryManager(
            new BackgroundJobStatusResponse
            {
                JobId = new BackgroundJobId("missing"),
                Status = BackgroundJobStatus.Lost,
                Found = false
            })));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "missing" };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Cancel_InaccessibleJob_ReturnsError()
    {
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeCancelManager(ActorRefs.Nobody, found: false)));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "abc123", ["Cancel"] = true };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Pending_receipt_exempts_only_the_same_verified_job_status_query()
    {
        var statuses = new Dictionary<string, BackgroundJobStatusResponse>(StringComparer.Ordinal)
        {
            ["job-a"] = Status("job-a", BackgroundJobStatus.Pending, found: true),
            ["private-job"] = Status("private-job", BackgroundJobStatus.Pending, found: false)
        };
        var manager = Sys.ActorOf(Props.Create(() => new DynamicJobManager(statuses, "test/thread")));
        var registry = new ToolRegistry();
        registry.WithBackgroundJobTools(manager);
        var executor = new DispatchingToolExecutor(registry, TestToolAccessPolicy.Create(new ToolConfig()));
        var tracker = new TurnStateTracker();

        await ExecuteAndObserveStatusAsync(tracker, executor, "pending-1", "job-a", expectPending: true);
        await ExecuteAndObserveStatusAsync(tracker, executor, "pending-2", "job-a", expectPending: true);

        var sameJob = PrepareStatus(executor, "pending-candidate", "job-a");
        var sameJobDecision = tracker.EvaluateBeforeDispatch(sameJob);
        Assert.Equal(ToolCycleDecisionKind.Execute, sameJobDecision.Kind);
        Assert.Contains("pending-candidate", sameJobDecision.ExemptCallIds);

        var otherJob = PrepareStatus(executor, "other-job", "other-job");
        var otherJobDecision = tracker.EvaluateBeforeDispatch(otherJob);
        Assert.Equal(ToolCycleDecisionKind.Execute, otherJobDecision.Kind);
        Assert.Empty(otherJobDecision.ExemptCallIds);

        // The same model-visible text cannot create the trusted receipt that the tool emits.
        ObserveUntrustedStatusText(tracker, executor, "claim-1", "job-a", "Job job-a: pending (0.0s elapsed)");
        ObserveUntrustedStatusText(tracker, executor, "claim-2", "job-a", "Job job-a: pending (0.0s elapsed)");
        var untrustedClaim = tracker.EvaluateBeforeDispatch(PrepareStatus(executor, "claim-candidate", "job-a"));
        Assert.Equal(ToolCycleDecisionKind.Correct, untrustedClaim.Kind);
        Assert.Empty(untrustedClaim.ExemptCallIds);

        var deniedTracker = new TurnStateTracker();
        await ExecuteAndObserveStatusAsync(
            deniedTracker, executor, "denied-1", "private-job", expectFound: false);
        await ExecuteAndObserveStatusAsync(
            deniedTracker, executor, "denied-2", "private-job", expectFound: false);
        var denied = deniedTracker.EvaluateBeforeDispatch(PrepareStatus(executor, "denied-candidate", "private-job"));
        Assert.Equal(ToolCycleDecisionKind.Correct, denied.Kind);
        Assert.Empty(denied.ExemptCallIds);
    }

    [Fact]
    public async Task Cancellation_has_no_pending_exception_and_terminal_outcome_resets_evidence()
    {
        var statuses = new Dictionary<string, BackgroundJobStatusResponse>(StringComparer.Ordinal)
        {
            ["job-a"] = Status("job-a", BackgroundJobStatus.Pending, found: true)
        };
        var manager = Sys.ActorOf(Props.Create(() => new DynamicJobManager(statuses, "test/thread")));
        var registry = new ToolRegistry();
        registry.WithBackgroundJobTools(manager);
        var executor = new DispatchingToolExecutor(registry, TestToolAccessPolicy.Create(new ToolConfig()));

        var cancellationTracker = new TurnStateTracker();
        await ExecuteAndObserveStatusAsync(
            cancellationTracker, executor, "cancel-1", "job-a", cancel: true);
        await ExecuteAndObserveStatusAsync(
            cancellationTracker, executor, "cancel-2", "job-a", cancel: true);
        var cancellation = cancellationTracker.EvaluateBeforeDispatch(
            PrepareStatus(executor, "cancel-candidate", "job-a", cancel: true));
        Assert.Equal(ToolCycleDecisionKind.Correct, cancellation.Kind);
        Assert.Empty(cancellation.ExemptCallIds);

        var outcomeTracker = new TurnStateTracker();
        await ExecuteAndObserveStatusAsync(
            outcomeTracker, executor, "pending-1", "job-a", expectPending: true);
        await ExecuteAndObserveStatusAsync(
            outcomeTracker, executor, "pending-2", "job-a", expectPending: true);
        statuses["job-a"] = Status("job-a", BackgroundJobStatus.Completed, found: true, exitCode: 0);

        await ExecuteAndObserveStatusAsync(outcomeTracker, executor, "completed-1", "job-a");
        var changedOutcome = outcomeTracker.EvaluateBeforeDispatch(PrepareStatus(executor, "completed-candidate", "job-a"));
        Assert.Equal(ToolCycleDecisionKind.Execute, changedOutcome.Kind);
        Assert.Empty(changedOutcome.ExemptCallIds);

        await ExecuteAndObserveStatusAsync(outcomeTracker, executor, "completed-2", "job-a");
        var repeatedTerminalOutcome = outcomeTracker.EvaluateBeforeDispatch(
            PrepareStatus(executor, "terminal-candidate", "job-a"));
        Assert.Equal(ToolCycleDecisionKind.Correct, repeatedTerminalOutcome.Kind);
        Assert.Empty(repeatedTerminalOutcome.ExemptCallIds);
    }

    private async Task ExecuteAndObserveStatusAsync(
        TurnStateTracker tracker,
        DispatchingToolExecutor executor,
        string callId,
        string jobId,
        bool expectPending = false,
        bool expectFound = true,
        bool cancel = false)
    {
        var call = StatusCall(callId, jobId, cancel);
        var batch = ToolCycleSignatureFactory.Prepare([call], executor);
        var context = MakeContext();
        var result = await executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken);
        Assert.Equal(expectPending, context.Receipt is ToolInvocationReceipt.PendingBackgroundJob);
        if (expectPending)
            Assert.StartsWith($"Job {jobId}: pending", result, StringComparison.Ordinal);
        else if (!expectFound)
            Assert.Contains("not found or not accessible", result, StringComparison.Ordinal);
        else if (cancel)
            Assert.Contains("Cancellation request sent", result, StringComparison.Ordinal);
        else
            Assert.StartsWith($"Job {jobId}: completed", result, StringComparison.Ordinal);

        tracker.ObserveCompleted(ToolCycleSignatureFactory.Complete(batch,
            new Dictionary<string, ToolCycleResult>(StringComparer.Ordinal)
            {
                [callId] = new ToolCycleResult(
                    context.Receipt?.Category ?? ToolInvocationOutcomeCategory.Success,
                    result)
                {
                    PendingJob = context.Receipt is ToolInvocationReceipt.PendingBackgroundJob
                }
            }));
    }

    private static void ObserveUntrustedStatusText(
        TurnStateTracker tracker,
        DispatchingToolExecutor executor,
        string callId,
        string jobId,
        string modelText)
    {
        var call = StatusCall(callId, jobId);
        var batch = ToolCycleSignatureFactory.Prepare([call], executor);
        tracker.ObserveCompleted(ToolCycleSignatureFactory.Complete(batch,
            new Dictionary<string, ToolCycleResult>(StringComparer.Ordinal)
            {
                [callId] = new ToolCycleResult(ToolInvocationOutcomeCategory.Success, modelText)
            }));
    }

    private static PreparedToolCycleBatch PrepareStatus(
        DispatchingToolExecutor executor,
        string callId,
        string jobId,
        bool cancel = false)
        => ToolCycleSignatureFactory.Prepare([StatusCall(callId, jobId, cancel)], executor);

    private static FunctionCallContent StatusCall(string callId, string jobId, bool cancel = false)
        => new(callId, CheckBackgroundJobTool.ToolName, new Dictionary<string, object?>
        {
            ["JobId"] = jobId,
            ["Cancel"] = cancel,
            ["_rationale"] = "Check the status of this background job."
        });

    private static BackgroundJobStatusResponse Status(
        string jobId,
        BackgroundJobStatus status,
        bool found,
        int? exitCode = null)
        => new()
        {
            JobId = new BackgroundJobId(jobId),
            Status = status,
            Found = found,
            Elapsed = TimeSpan.Zero,
            ExitCode = exitCode
        };

    private sealed class DynamicJobManager : ReceiveActor
    {
        public DynamicJobManager(IReadOnlyDictionary<string, BackgroundJobStatusResponse> statuses, string authorizedSessionId)
        {
            Receive<QueryBackgroundJob>(query =>
            {
                var authorized = query.SessionId.Value == authorizedSessionId
                    && query.Audience == TrustAudience.Personal
                    && query.Boundary == TrustBoundary.Personal;
                Sender.Tell(authorized && statuses.TryGetValue(query.JobId.Value, out var response)
                    ? response
                    : Status(query.JobId.Value, BackgroundJobStatus.Lost, found: false));
            });
            Receive<CancelBackgroundJob>(command =>
            {
                var authorized = command.SessionId.Value == authorizedSessionId
                    && command.Audience == TrustAudience.Personal
                    && command.Boundary == TrustBoundary.Personal;
                Sender.Tell(new BackgroundJobCancelResponse(command.JobId, Found: authorized));
            });
        }
    }

    private sealed class FakeQueryManager : ReceiveActor
    {
        public FakeQueryManager(BackgroundJobStatusResponse response)
        {
            Receive<QueryBackgroundJob>(_ => Sender.Tell(response));
        }
    }

    private sealed class FakeCancelManager : ReceiveActor
    {
        public FakeCancelManager(IActorRef probe, bool found = true)
        {
            Receive<CancelBackgroundJob>(cmd =>
            {
                probe.Forward(cmd);
                Sender.Tell(new BackgroundJobCancelResponse(cmd.JobId, found));
            });
        }
    }
}
