// -----------------------------------------------------------------------
// <copyright file="PidFileWatchdogServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class PidFileWatchdogServiceTests : IDisposable
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(100);

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeApplicationLifetime _lifetime;

    public PidFileWatchdogServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _lifetime = new FakeApplicationLifetime();
    }

    [Fact]
    public async Task DoesNotShutdown_WhenPidFileExists()
    {
        File.WriteAllText(_paths.PidFilePath, Environment.ProcessId.ToString());

        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        // Let several poll cycles pass
        await WaitUntilAsync(() => false, timeout: FastPoll * 8);

        Assert.False(_lifetime.StopRequested);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task InitiatesShutdown_WhenPidFileDeleted()
    {
        File.WriteAllText(_paths.PidFilePath, Environment.ProcessId.ToString());

        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        File.Delete(_paths.PidFilePath);

        Assert.True(await WaitUntilAsync(() => _lifetime.StopRequested, timeout: TimeSpan.FromSeconds(5)));

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task InitiatesShutdown_WhenPidFileNeverExisted()
    {
        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => _lifetime.StopRequested, timeout: TimeSpan.FromSeconds(5)));

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    private PidFileWatchdogService CreateService()
    {
        return new PidFileWatchdogService(
            _paths,
            _lifetime,
            NullLogger<PidFileWatchdogService>.Instance,
            FastPoll);
    }

    /// <summary>
    /// Polls a condition until it becomes true or the timeout expires.
    /// Returns true if the condition was met, false on timeout.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                if (condition())
                    return true;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { } // slopwatch-ignore: SW003 timeout expired — fall through to final condition check
        return condition();
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }
}
