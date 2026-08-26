// -----------------------------------------------------------------------
// <copyright file="PidFileWatchdogServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class PidFileWatchdogServiceTests : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeApplicationLifetime _lifetime;
    private readonly FakeTimeProvider _timeProvider = new();

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

        _timeProvider.Advance(PollInterval * 8);

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
        _timeProvider.Advance(PollInterval);

        await _lifetime.ShutdownRequested.WaitAsync(TestContext.Current.CancellationToken);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task InitiatesShutdown_WhenPidFileNeverExisted()
    {
        var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);
        _timeProvider.Advance(PollInterval);

        await _lifetime.ShutdownRequested.WaitAsync(TestContext.Current.CancellationToken);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    private PidFileWatchdogService CreateService()
    {
        return new PidFileWatchdogService(
            _paths,
            _lifetime,
            NullLogger<PidFileWatchdogService>.Instance,
            _timeProvider,
            PollInterval);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _shutdownRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool StopRequested => _shutdownRequested.Task.IsCompleted;
        public Task ShutdownRequested => _shutdownRequested.Task;

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _shutdownRequested.TrySetResult();
    }
}
