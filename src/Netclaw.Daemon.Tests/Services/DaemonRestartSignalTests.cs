// -----------------------------------------------------------------------
// <copyright file="DaemonRestartSignalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class DaemonRestartSignalTests
{
    [Fact]
    public void Generation_StartsAtZero_AndAdvancesMonotonically()
    {
        // The wizard's readiness gate relies on a strictly-increasing generation to tell a
        // reloaded daemon from a still-draining one (#1302) — verify the counter only ever
        // goes up, never resets.
        var signal = new DaemonRestartSignal();
        Assert.Equal(0, signal.Generation);

        signal.AdvanceGeneration();
        Assert.Equal(1, signal.Generation);

        signal.AdvanceGeneration();
        Assert.Equal(2, signal.Generation);
    }

    [Fact]
    public void Reset_ClearsRestartFlag_ButNotGeneration()
    {
        // Reset() clears the restart-requested flag at the top of each loop iteration; it
        // must NOT touch the generation, or a reused signal would let a stale daemon
        // masquerade as restarted.
        var signal = new DaemonRestartSignal();
        signal.AdvanceGeneration();
        signal.RequestRestart();

        signal.Reset();

        Assert.False(signal.RestartRequested);
        Assert.Equal(1, signal.Generation);
    }
}
