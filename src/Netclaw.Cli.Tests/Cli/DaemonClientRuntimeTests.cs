// -----------------------------------------------------------------------
// <copyright file="DaemonClientRuntimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Cli.Daemon;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientRuntimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_calls_share_one_runtime_start_without_blocking_callers(bool failStart)
    {
        using var releaseStart = new ManualResetEventSlim(false);
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startFailure = new InvalidOperationException("ActorSystem startup failed.");
        var starts = 0;
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient("http://localhost", transport, (name, config) =>
        {
            Interlocked.Increment(ref starts);
            startEntered.TrySetResult();
            releaseStart.Wait(token);
            if (failStart) throw startFailure;
            return ActorSystem.Create(name, config);
        }, TimeProvider.System, [TimeSpan.Zero], Deadline);

        Assert.False(client.HasLocalRuntime);
        Assert.Equal(0, starts);
        var first = StartCaller(() => client.ConnectAsync(token));
        Task<Task>? second = null;
        try
        {
            await startEntered.Task.WaitAsync(Deadline, token);
            second = StartCaller(() =>
            {
                secondEntered.TrySetResult();
                return client.ConnectAsync(token);
            });
            await secondEntered.Task.WaitAsync(Deadline, token);

            // The test holds startup until both synchronous API calls return their task handles.
            await AssertCallerReturnedAsync(second, "The second caller");
            await AssertCallerReturnedAsync(first, "The first caller");
            Assert.False((await first).IsCompleted);
            Assert.False((await second).IsCompleted);
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal(0, transport.StartAttempts);
        }
        finally
        {
            // Release before disposal if the regression assertion fails.
            releaseStart.Set();
            await AssertOutcomeAsync(first, failStart ? startFailure : null);
            if (second is not null) await AssertOutcomeAsync(second, failStart ? startFailure : null);
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal(failStart ? 0 : 1, transport.StartAttempts);
            Assert.Equal(!failStart, client.HasLocalRuntime);
        }
    }

    private static Task<Task> StartCaller(Func<Task> call)
        => Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task AssertCallerReturnedAsync(Task<Task> caller, string name)
    {
        try
        {
            await caller.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{name} blocked before it returned a task while the test held ActorSystem startup.");
        }
    }

    private static async Task AssertOutcomeAsync(Task<Task> caller, Exception? expectedFailure)
    {
        var error = await Record.ExceptionAsync(async () =>
        {
            var request = await caller.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await request.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        });
        Assert.Same(expectedFailure, error);
    }
}
