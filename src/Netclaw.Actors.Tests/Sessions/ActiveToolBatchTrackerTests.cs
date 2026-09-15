// -----------------------------------------------------------------------
// <copyright file="ActiveToolBatchTrackerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ActiveToolBatchTrackerTests
{
    [Fact]
    public void Start_StoresRequestedBatchSizeAndClearsDispatchState()
    {
        var tracker = new ActiveToolBatchTracker();
        tracker.Start(
            [
                new FunctionCallContent("call-1", "probe"),
                new FunctionCallContent("call-2", "probe")
            ],
            preparedCycleBatch: null);

        Assert.Equal(2, tracker.BatchSize);
        Assert.False(tracker.HasReachedDispatch);

        tracker.MarkDispatched();
        Assert.True(tracker.HasReachedDispatch);

        tracker.Start([new FunctionCallContent("call-3", "probe")], preparedCycleBatch: null);

        Assert.Equal(1, tracker.BatchSize);
        Assert.False(tracker.HasReachedDispatch);
    }

    [Fact]
    public void Clear_RemovesBatchAndDispatchState()
    {
        var tracker = new ActiveToolBatchTracker();
        tracker.Start([new FunctionCallContent("call-1", "probe")], preparedCycleBatch: null);
        tracker.MarkDispatched();

        tracker.Clear();

        Assert.Equal(0, tracker.BatchSize);
        Assert.False(tracker.HasReachedDispatch);
    }

    [Fact]
    public void MarkDispatched_WithoutActiveBatchFailsLoudly()
    {
        var tracker = new ActiveToolBatchTracker();

        Assert.Throws<InvalidOperationException>(tracker.MarkDispatched);
    }
}
