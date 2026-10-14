// -----------------------------------------------------------------------
// <copyright file="ChildRunExecutionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed class ChildRunExecutionTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Fact]
    public void Close_ack_rejects_later_dispatch_without_requesting_cancellation()
    {
        using var execution = new ChildRunExecution(new SubAgentRunId("close-run"), ActorRefs.Nobody);

        execution.Close();

        Assert.False(execution.Token.IsCancellationRequested);
        var rejected = execution.Enter(() => Task.FromResult(true));
        Assert.True(rejected.IsCanceled);
    }

    [Fact]
    public async Task Close_ack_waits_for_an_admitted_dispatch_delegate()
    {
        using var execution = new ChildRunExecution(new SubAgentRunId("close-race-run"), ActorRefs.Nobody);
        using var delegateEntered = new ManualResetEventSlim();
        using var releaseDelegate = new ManualResetEventSlim();
        using var closeStarted = new ManualResetEventSlim();
        using var closeCompleted = new ManualResetEventSlim();
        using var enterCompleted = new ManualResetEventSlim();
        var errors = new ConcurrentQueue<Exception>();

        var entrant = new Thread(() =>
        {
            try
            {
                var dispatch = execution.Enter(() =>
                {
                    delegateEntered.Set();
                    if (!releaseDelegate.Wait(Ceiling))
                        throw new TimeoutException("The test did not release the dispatch delegate.");
                    return Task.FromResult(true);
                });
                Assert.True(dispatch.GetAwaiter().GetResult());
            }
            catch (Exception error)
            {
                errors.Enqueue(error);
            }
            finally
            {
                enterCompleted.Set();
            }
        });
        entrant.Start();
        Assert.True(delegateEntered.Wait(Ceiling, TestContext.Current.CancellationToken));

        var closer = new Thread(() =>
        {
            closeStarted.Set();
            execution.Close();
            closeCompleted.Set();
        });
        closer.Start();

        try
        {
            Assert.True(closeStarted.Wait(Ceiling, TestContext.Current.CancellationToken));
            await AwaitAssertAsync(() =>
            {
                Assert.False(closeCompleted.IsSet, "Close returned while an admitted dispatch held the gate.");
                Assert.True((closer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    "The close thread did not wait for the admitted dispatch gate.");
            }, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseDelegate.Set();
            entrant.Join(Ceiling);
            closer.Join(Ceiling);
        }

        Assert.True(enterCompleted.Wait(Ceiling, TestContext.Current.CancellationToken));
        Assert.True(closeCompleted.Wait(Ceiling, TestContext.Current.CancellationToken));
        Assert.Empty(errors);
        var rejected = execution.Enter(() => Task.FromResult(true));
        Assert.True(rejected.IsCanceled);
    }

    [Fact]
    public async Task Stop_ack_waits_for_the_registered_cancellation_callback()
    {
        using var execution = new ChildRunExecution(new SubAgentRunId("cancel-run"), ActorRefs.Nobody);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = execution.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        try
        {
            var stopped = execution.StopAsync();
            await callbackEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.False(stopped.IsCompleted);
            Assert.True(execution.Token.IsCancellationRequested);

            releaseCallback.TrySetResult();
            await stopped.WaitAsync(Ceiling, TestContext.Current.CancellationToken);

            var rejected = execution.Enter(() => Task.FromResult(true));
            Assert.True(rejected.IsCanceled);
        }
        finally
        {
            releaseCallback.TrySetResult();
        }
    }
}
