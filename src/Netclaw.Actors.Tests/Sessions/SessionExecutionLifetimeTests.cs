// -----------------------------------------------------------------------
// <copyright file="SessionExecutionLifetimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionExecutionLifetimeTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    /// <summary>
    /// running-child-drain-park design D4. The drain acknowledgement waits while a live child exists and the child wait
    /// has not ended. With no drain, no live child, or an ended wait, it does not wait.
    /// Planted bug: the session acknowledges before the child parks, or a stuck child holds the drain past the deadline.
    /// </summary>
    [Theory]
    [InlineData("no-drain", false, true, false, false)]
    [InlineData("live-child", true, true, false, true)]
    [InlineData("no-live-child", true, false, false, false)]
    [InlineData("wait-ended", true, true, true, false)]
    public void The_drain_acknowledgement_waits_only_for_live_children_inside_the_wait(string row, bool drain,
        bool liveChildren, bool waitEnded, bool expected)
        => Assert.Equal((row, expected), (row, SessionExecutionLifetime.WaitsForChildren(drain, liveChildren, waitEnded)));

    [Theory]
    [InlineData("success")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task Model_stop_waits_for_the_exact_task_and_preserves_its_outcome(string outcome)
    {
        // Arrange
        var owner = CreateOwner();
        var call = owner.BeginModel();
        // Inline continuations preserve the foreign actor context for the implicit Tell check.
        var work = new TaskCompletionSource();
        owner.AttachModel(call.CallId, work.Task);
        var failure = new IOException("model task failed");

        // Act: Request the stop while the task remains incomplete.
        var requested = owner.RequestModelStop(call.CallId);
        var duplicateRequest = owner.RequestModelStop(call.CallId);
        var wrongStop = owner.AcceptModelStop(new ModelStoppedForRestart(call.CallId - 1, null));
        var resultDuringStop = owner.AcceptModelResult(call.CallId);
        var progressDuringStop = owner.AcceptModelProgress(
            call.CallId, StreamActivity.Answer, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        TestActor.Tell("stop-request-returned");

        // Assert: The mailbox fence detects a synchronous premature acknowledgement.
        Assert.True(requested);
        Assert.True(call.Token.IsCancellationRequested);
        Assert.False(work.Task.IsCompleted);
        Assert.False(duplicateRequest);
        Assert.False(wrongStop);
        Assert.False(resultDuringStop);
        Assert.False(progressDuringStop);
        Assert.False(owner.CanDispatchPreparedModel(call.CallId));
        await ExpectMsgAsync("stop-request-returned", cancellationToken: TestContext.Current.CancellationToken);

        // Act: Release the exact task and receive its stop acknowledgement.
        var completer = CompleteInForeignActor(() => Complete(work, outcome, failure));
        var stopped = await ExpectMsgAsync<ModelStoppedForRestart>(cancellationToken: TestContext.Current.CancellationToken);
        var stopSender = LastSender;
        await ExpectTerminatedAsync(completer, cancellationToken: TestContext.Current.CancellationToken);
        var accepted = owner.AcceptModelStop(stopped);
        var duplicateStop = owner.AcceptModelStop(stopped);
        var staleResult = owner.AcceptModelResult(call.CallId);

        // Assert
        Assert.Equal(call.CallId, stopped.CallId);
        if (outcome == "cancelled")
            Assert.IsAssignableFrom<OperationCanceledException>(stopped.Failure);
        else if (outcome == "failed")
            Assert.Same(failure, stopped.Failure);
        else
            Assert.Null(stopped.Failure);
        Assert.True(accepted);
        Assert.False(owner.HasLiveModel);
        Assert.False(duplicateStop);
        Assert.False(staleResult);
        Assert.Equal(Sys.DeadLetters, stopSender);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task Tool_stop_waits_for_the_exact_task_and_treats_cancellation_as_a_normal_stop(string outcome)
    {
        // Arrange
        var owner = CreateOwner();
        var token = owner.BeginTool();
        // Inline continuations preserve the foreign actor context for the implicit Tell check.
        var work = new TaskCompletionSource();
        owner.AttachTool(work.Task);
        var failure = new IOException("tool task failed");

        // Act: Request the stop while the task remains incomplete.
        var requested = owner.RequestToolStop();
        var duplicateRequest = owner.RequestToolStop();
        var wrongStop = owner.AcceptToolStop(new ToolPipelineStoppedForRestart(Task.CompletedTask, null));
        TestActor.Tell("stop-request-returned");

        // Assert: The mailbox fence detects a synchronous premature acknowledgement.
        Assert.True(requested);
        Assert.True(token.IsCancellationRequested);
        Assert.False(work.Task.IsCompleted);
        Assert.False(duplicateRequest);
        Assert.False(wrongStop);
        await ExpectMsgAsync("stop-request-returned", cancellationToken: TestContext.Current.CancellationToken);

        // Act: Release the exact task and clear its accepted stop.
        var completer = CompleteInForeignActor(() => Complete(work, outcome, failure));
        var stopped = await ExpectMsgAsync<ToolPipelineStoppedForRestart>(cancellationToken: TestContext.Current.CancellationToken);
        var stopSender = LastSender;
        await ExpectTerminatedAsync(completer, cancellationToken: TestContext.Current.CancellationToken);
        var accepted = owner.AcceptToolStop(stopped);
        owner.ClearToolTracking();
        owner.ReleaseTool();
        var staleStop = owner.AcceptToolStop(stopped);

        // Assert
        Assert.Same(work.Task, stopped.WorkTask);
        Assert.Same(outcome == "failed" ? failure : null, stopped.Failure);
        Assert.True(accepted);
        Assert.False(staleStop);
        Assert.Equal(Sys.DeadLetters, stopSender);
    }

    [Fact]
    public async Task Model_stop_before_dispatch_rejects_the_prepared_call_and_allows_a_new_call()
    {
        // Arrange
        var owner = CreateOwner();
        var prepared = owner.BeginModel();

        // Act: Stop the call before a provider task exists.
        var requested = owner.RequestModelStop(prepared.CallId);
        var stopped = await ExpectMsgAsync<ModelStoppedForRestart>(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(requested);
        Assert.True(prepared.Token.IsCancellationRequested);
        Assert.Null(stopped.Failure);
        Assert.False(owner.CanDispatchPreparedModel(prepared.CallId));

        // Act: Start the next call and deliver old replies.
        var accepted = owner.AcceptModelStop(stopped);
        var next = owner.BeginModel();
        var staleResult = owner.AcceptModelResult(prepared.CallId);
        var staleProgress = owner.AcceptModelProgress(
            prepared.CallId, StreamActivity.Answer, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(accepted);
        Assert.True(owner.CanDispatchPreparedModel(next.CallId));
        Assert.False(staleResult);
        Assert.False(staleProgress);
        Assert.False(owner.HasContentStreamed);
        Assert.False(next.Token.IsCancellationRequested);

        // Cleanup
        owner.ReleaseAll();
    }

    [Fact]
    public void A_new_model_call_replaces_a_cancelled_task_that_does_not_return()
    {
        // Arrange
        var owner = CreateOwner();
        var old = owner.BeginModel();
        var oldWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AttachModel(old.CallId, oldWork.Task);
        owner.StopModelForTimeout();

        // Act
        var next = owner.BeginModel();
        owner.AttachModel(next.CallId, Task.CompletedTask);
        var staleResult = owner.AcceptModelResult(old.CallId);
        var canDispatch = owner.CanDispatchPreparedModel(next.CallId);
        var accepted = owner.AcceptModelResult(next.CallId);

        // Assert
        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(oldWork.Task.IsCompleted);
        Assert.False(staleResult);
        Assert.True(canDispatch);
        Assert.True(accepted);

        // Cleanup
        oldWork.SetResult();
    }

    [Fact]
    public async Task A_new_tool_batch_replaces_a_failed_task_that_does_not_return()
    {
        // Arrange
        var owner = CreateOwner();
        var oldToken = owner.BeginTool();
        var oldWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AttachTool(oldWork.Task);
        owner.OnToolFailure();

        // Act
        var token = owner.BeginTool();
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AttachTool(work.Task);
        var requested = owner.RequestToolStop();
        work.SetResult();
        var stopped = await ExpectMsgAsync<ToolPipelineStoppedForRestart>(cancellationToken: TestContext.Current.CancellationToken);
        var accepted = owner.AcceptToolStop(stopped);

        // Assert
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(oldWork.Task.IsCompleted);
        Assert.True(requested);
        Assert.True(token.IsCancellationRequested);
        Assert.Same(work.Task, stopped.WorkTask);
        Assert.True(accepted);

        // Cleanup
        owner.ClearToolTracking();
        owner.ReleaseAll();
        oldWork.SetResult();
    }

    [Fact]
    public async Task Synthetic_batch_completion_without_a_task_does_not_block_the_next_real_task()
    {
        // Arrange
        var owner = CreateOwner();

        // Act: Complete a correction that has no pipeline task.
        owner.OnToolBatchMessage();
        var unrelatedStop = owner.AcceptToolStop(new ToolPipelineStoppedForRestart(Task.CompletedTask, null));

        // Assert
        Assert.False(owner.CanRequestToolStop());
        Assert.False(unrelatedStop);

        // Act: Start and stop a real pipeline task.
        var token = owner.BeginTool();
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AttachTool(work.Task);
        var requested = owner.RequestToolStop();
        work.SetResult();
        var stopped = await ExpectMsgAsync<ToolPipelineStoppedForRestart>(cancellationToken: TestContext.Current.CancellationToken);
        var accepted = owner.AcceptToolStop(stopped);

        // Assert
        Assert.True(requested);
        Assert.Same(work.Task, stopped.WorkTask);
        Assert.True(token.IsCancellationRequested);
        Assert.True(accepted);

        // Cleanup
        owner.ClearToolTracking();
        owner.ReleaseAll();
    }

    [Fact]
    public async Task Stale_tool_stop_reply_cannot_settle_the_next_tool_operation()
    {
        // Arrange: Stop one tool batch.
        var owner = CreateOwner();
        var oldTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginTool();
        owner.AttachTool(oldTool.Task);
        Assert.True(owner.RequestToolStop());
        oldTool.SetResult();
        var oldToolStop = await ExpectMsgAsync<ToolPipelineStoppedForRestart>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(owner.AcceptToolStop(oldToolStop));
        owner.ClearToolTracking();
        owner.ReleaseTool();

        // Act: Start a new batch and request its stop.
        var newToolWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginTool();
        owner.AttachTool(newToolWork.Task);
        Assert.True(owner.RequestToolStop());

        var staleToolAccepted = owner.AcceptToolStop(oldToolStop);

        // Assert: Only the current task acknowledgement can settle the stop.
        Assert.False(staleToolAccepted);
        Assert.False(newToolWork.Task.IsCompleted);
        newToolWork.SetResult();
        var newToolStop = await ExpectMsgAsync<ToolPipelineStoppedForRestart>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(newToolWork.Task, newToolStop.WorkTask);
        Assert.True(owner.AcceptToolStop(newToolStop));
        owner.ClearToolTracking();
        owner.ReleaseAll();
    }

    private SessionExecutionLifetime CreateOwner()
        => new(new ProcessingWatchdogTests.RecordingTimerScheduler(), TestActor);

    private static void Complete(TaskCompletionSource work, string outcome, Exception failure)
    {
        switch (outcome)
        {
            case "success":
                work.SetResult();
                break;
            case "cancelled":
                work.SetCanceled();
                break;
            case "failed":
                work.SetException(failure);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown task outcome");
        }
    }

    private IActorRef CompleteInForeignActor(Action action)
    {
        var completer = Sys.ActorOf(Props.Create(() => new ActionActor(action)));
        Watch(completer);
        completer.Tell(new RunAction(), TestActor);
        return completer;
    }

    private sealed record RunAction;

    private sealed class ActionActor : ReceiveActor
    {
        public ActionActor(Action action)
        {
            Receive<RunAction>(_ =>
            {
                action();
                Context.Stop(Self);
            });
        }
    }
}
