// -----------------------------------------------------------------------
// <copyright file="DaemonManagerGracefulShutdownTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Covers the canary daemon-stop finding: <c>systemctl --user stop netclaw.service</c> landed
/// in <c>failed (Result: signal)</c> because <see cref="DaemonManager.StopAsync"/>'s SIGTERM
/// grace period (previously a hardcoded 10s) was far shorter than the ~200s the daemon's own
/// Akka CoordinatedShutdown session-drain phase is deliberately allotted — so the CLI itself
/// gave up and force-killed the daemon long before a legitimately slow (in-flight LLM call)
/// graceful shutdown could finish. It still died, so `netclaw daemon stop` (ExecStop) reported
/// success, but via SIGKILL rather than a clean exit — exactly what systemd's
/// <c>failed (Result: signal)</c> was observing.
///
/// These tests exercise the two testable halves of the fix: (1) the internal
/// <see cref="DaemonManager.WaitForExitAsync"/> poll now honors an injected
/// <see cref="TimeProvider"/> end-to-end (not just for its deadline math), so the up-to-200s
/// wait can be driven with a <see cref="FakeTimeProvider"/> instead of a real sleep; and
/// (2) the generated systemd unit's <c>TimeoutStopSec=</c> stays in lockstep with
/// <see cref="DaemonConfig.GracefulShutdownBudget"/> so systemd itself never SIGKILLs the whole
/// cgroup out from under a still-legitimately-waiting <c>ExecStop=</c>.
/// </summary>
public sealed class DaemonManagerGracefulShutdownTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DaemonManagerGracefulShutdownTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task WaitForExitAsync_ReturnsTrue_Immediately_WhenProcessAlreadyExited()
    {
        var manager = new DaemonManager(_paths, TimeProvider.System);
        using var exited = StartAndWaitForRealExit();

        var result = await manager.WaitForExitAsync(exited, TimeSpan.FromSeconds(200), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForExitAsync_ReturnsFalse_OnceVirtualClockPassesTimeout_WithoutRealTimeDelay()
    {
        var fakeTime = new FakeTimeProvider();
        var manager = new DaemonManager(_paths, fakeTime);
        // The current test process never exits mid-test — stands in for a daemon still
        // draining a stuck/slow session.
        var neverExits = Process.GetCurrentProcess();

        var waitTask = manager.WaitForExitAsync(neverExits, DaemonConfig.GracefulShutdownBudget, CancellationToken.None);

        // A single jump past the full budget — if the poll delay inside WaitForExitAsync were
        // still a bare real-time `Task.Delay(200)` (the pre-fix shape), this test would need to
        // actually wait out that real time instead of resolving from one Advance() call.
        fakeTime.Advance(DaemonConfig.GracefulShutdownBudget + TimeSpan.FromSeconds(1));

        var result = await waitTask;

        Assert.False(result);
    }

    [Fact]
    public void BuildDaemonUnitContent_SetsTimeoutStopSec_ConsistentWithGracefulShutdownBudget()
    {
        var unit = DaemonManager.BuildDaemonUnitContent(
            "/opt/netclaw/netclawd", "/opt/netclaw/netclaw", "/opt/netclaw/daemon.env");

        var expectedTimeoutStopSec = (int)(DaemonConfig.GracefulShutdownBudget + TimeSpan.FromSeconds(30)).TotalSeconds;

        Assert.Contains($"TimeoutStopSec={expectedTimeoutStopSec}", unit, StringComparison.Ordinal);

        // TimeoutStopSec bounds the ENTIRE stop job (ExecStop's own runtime included), so it
        // must leave systemd comfortably behind netclaw daemon stop's own SIGTERM-wait ceiling
        // — otherwise systemd would SIGKILL the cgroup mid-ExecStop before the CLI's own,
        // more-informative timeout/escalation logic ever gets to run.
        Assert.True(
            expectedTimeoutStopSec > DaemonConfig.GracefulShutdownBudget.TotalSeconds,
            "Unit TimeoutStopSec must exceed DaemonManager.StopAsync's own SIGTERM wait.");
    }

    private static Process StartAndWaitForRealExit()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var process = Process.Start(psi)!;
        process.WaitForExit();
        return process;
    }
}
