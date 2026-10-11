// -----------------------------------------------------------------------
// <copyright file="BackgroundChildReportGraceAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class BackgroundChildReportGraceAdversarialTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_grace_settles_without_wait_for_an_uncooperative_report_operation(bool synchronousHold)
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asyncRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var syncRelease = new ManualResetEventSlim();
        async Task Operation(CancellationToken token)
        {
            entered.TrySetResult(token);
            try
            {
                if (synchronousHold)
                    syncRelease.Wait(TestContext.Current.CancellationToken);
                else
                    await asyncRelease.Task;
            }
            finally { completed.TrySetResult(); }
        }
        var finalization = LlmSessionActor.CompleteChildReportWithinGraceAsync(Operation, time);
        try
        {
            var token = await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.False(token.IsCancellationRequested);
            time.Advance(TimeSpan.FromSeconds(4));
            Assert.False(finalization.IsCompleted);
            Assert.False(completed.Task.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalization.WaitAsync(Ceiling,
                TestContext.Current.CancellationToken));
            Assert.True(token.IsCancellationRequested);
            Assert.False(completed.Task.IsCompleted);
        }
        finally
        {
            syncRelease.Set();
            asyncRelease.TrySetResult();
            await completed.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_report_operation_preserves_its_actual_success_or_failure(bool fail)
    {
        var time = new FakeTimeProvider();
        var executed = false;
        var expected = new IOException("The report writer failed before an asynchronous boundary.");
        Task Operation(CancellationToken token)
        {
            Assert.False(token.IsCancellationRequested);
            executed = true;
            if (fail) throw expected;
            return Task.CompletedTask;
        }
        var finalization = LlmSessionActor.CompleteChildReportWithinGraceAsync(Operation, time);
        if (fail)
            Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => finalization.WaitAsync(Ceiling,
                TestContext.Current.CancellationToken)));
        else
            await finalization.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        Assert.True(executed);
    }
}
