// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherRealFileEventsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Services;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

// The debounce uses the real clock. Each wait ends on an event, not on a sleep.
public sealed class ConfigWatcherRealFileEventsTests : IDisposable
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly SignalingRestartCoordinator _restart = new();
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherRealFileEventsTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        var services = new ServiceCollection();
        services.AddSingleton(_paths);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLlmProviders();
        _sut = new ConfigWatcherService(
            _paths,
            TimeProvider.System,
            _restart,
            new RejectedConfigState(),
            services.BuildServiceProvider().GetServices<ILlmProviderPlugin>(),
            NullLogger<ConfigWatcherService>.Instance);
    }

    [Fact]
    public async Task An_OAuth_token_refresh_restarts_nothing_and_a_netclaw_json_write_restarts_once()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        var delivered = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal)
        {
            ["secrets.json"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["oauth-state.json"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        using var deliveryWitness = new FileSystemWatcher(_paths.ConfigDirectory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
        };
        void Signal(object sender, FileSystemEventArgs e)
        {
            if (e.Name is not null && delivered.TryGetValue(e.Name, out var signal))
                signal.TrySetResult();
        }

        deliveryWitness.Changed += Signal;
        deliveryWitness.Created += Signal;
        deliveryWitness.Renamed += (sender, e) => Signal(sender, e);
        deliveryWitness.EnableRaisingEvents = true;
        await _sut.StartAsync(TestContext.Current.CancellationToken);

        OAuthTokenPersistence.PersistTokens(
            _paths,
            "copilot",
            new OAuthDeviceFlowResult(
                new SensitiveString("access-1"),
                new SensitiveString("refresh-1"),
                DateTimeOffset.Parse("2026-06-01T01:00:00+00:00")),
            new NullSecretsProtector());
        await Task.WhenAll(delivered.Values.Select(signal => signal.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken)));
        await _sut.PendingReload;

        Assert.Equal(0, _restart.RequestCount);
        Assert.True(File.Exists(_paths.OAuthStatePath));

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Port": 9999, "ExposureMode": "reverse-proxy" } }""");
        await _restart.FirstRequest.WaitAsync(EventTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, _restart.RequestCount);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dir.Dispose();
    }

    private sealed class SignalingRestartCoordinator : IDaemonRestartCoordinator
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int RequestCount => Volatile.Read(ref _count);

        public Task FirstRequest => _first.Task;

        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            _first.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
