// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncActorTests : IDisposable
{
    private readonly ActorSystem _system = ActorSystem.Create($"skill-sync-tests-{Guid.NewGuid():N}");
    private readonly DisposableTempDir _temp = new();

    [Fact]
    public async Task Concurrent_requests_join_the_startup_pass()
    {
        var runner = new ControlledRunner();
        var logger = new JoinSignalLogger(2);
        var actor = CreateActor(runner, logger);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.Started.Task.WaitAsync(cancellationToken);

        var first = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.Instance,
            cancellationToken);
        var second = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.Instance,
            cancellationToken);
        await logger.TargetReached.Task.WaitAsync(cancellationToken);
        runner.Release();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].PassId, results[1].PassId);
        Assert.Equal(1, runner.PassCount);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_the_shared_pass()
    {
        var runner = new ControlledRunner();
        var logger = new JoinSignalLogger(2);
        var actor = CreateActor(runner, logger);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.Started.Task.WaitAsync(cancellationToken);

        using var canceledWait = new CancellationTokenSource();
        var canceled = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.Instance,
            canceledWait.Token);
        var healthy = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.Instance,
            cancellationToken);
        await logger.TargetReached.Task.WaitAsync(cancellationToken);

        canceledWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(runner.LifetimeToken.IsCancellationRequested);

        runner.Release();
        Assert.NotNull(await healthy);
        Assert.Equal(1, runner.PassCount);
    }

    [Fact]
    public async Task Actor_stop_cancels_the_pass_and_fails_joined_requests()
    {
        var runner = new ControlledRunner();
        var logger = new JoinSignalLogger(1);
        var actor = CreateActor(runner, logger);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.Started.Task.WaitAsync(cancellationToken);

        var request = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.Instance,
            cancellationToken);
        await logger.TargetReached.Task.WaitAsync(cancellationToken);
        actor.Tell(PoisonPill.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        await runner.Canceled.Task.WaitAsync(cancellationToken);
        Assert.True(runner.LifetimeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Retry_request_queues_after_an_active_ordinary_pass()
    {
        var runner = new SequencedRunner();
        var actor = CreateActor(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerFeedSkillSyncActor>.Instance);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.FirstStarted.Task.WaitAsync(cancellationToken);

        var retry = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.RetryRejectedCommits,
            cancellationToken);
        runner.ReleaseFirst.TrySetResult();
        await runner.SecondStarted.Task.WaitAsync(cancellationToken);

        Assert.Equal([false, true], runner.RetryModes);
        runner.ReleaseSecond.TrySetResult();
        Assert.NotNull(await retry);
    }

    [Fact]
    public async Task Named_plugin_request_waits_for_complete_pass_and_gets_its_own_result()
    {
        var runner = new SequencedRunner();
        var actor = CreateActor(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerFeedSkillSyncActor>.Instance);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.FirstStarted.Task.WaitAsync(cancellationToken);

        var named = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.ForPlugin("team-tools", retryRejected: false), cancellationToken);
        runner.ReleaseFirst.TrySetResult();
        await runner.SecondStarted.Task.WaitAsync(cancellationToken);

        Assert.Equal(ServerFeedSkillSyncActor.SyncScope.Complete, runner.Requests[0].Scope);
        Assert.Equal(ServerFeedSkillSyncActor.SyncScope.Plugin, runner.Requests[1].Scope);
        Assert.Equal("team-tools", runner.Requests[1].PluginId);
        runner.ReleaseSecond.TrySetResult();
        Assert.Equal("2", (await named).PassId);
    }

    [Fact]
    public async Task Equal_named_requests_share_one_queued_pass()
    {
        var runner = new SequencedRunner();
        var actor = CreateActor(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerFeedSkillSyncActor>.Instance);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.FirstStarted.Task.WaitAsync(cancellationToken);

        var request = ServerFeedSkillSyncActor.Run.ForPlugin("team-tools", retryRejected: false);
        var first = actor.Ask<SkillSyncResult.Response>(request, cancellationToken);
        var second = actor.Ask<SkillSyncResult.Response>(request, cancellationToken);
        runner.ReleaseFirst.TrySetResult();
        await runner.SecondStarted.Task.WaitAsync(cancellationToken);
        runner.ReleaseSecond.TrySetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal("2", results[0].PassId);
        Assert.Equal(results[0].PassId, results[1].PassId);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task Distinct_queued_scopes_have_a_fixed_limit()
    {
        var runner = new ControlledRunner();
        var logger = new QueueSignalLogger(44);
        var actor = CreateActor(runner, logger);
        var cancellationToken = TestContext.Current.CancellationToken;
        await runner.Started.Task.WaitAsync(cancellationToken);

        var queued = Enumerable.Range(0, 44)
            .Select(index => actor.Ask<SkillSyncResult.Response>(
                ServerFeedSkillSyncActor.Run.ForPlugin($"source-{index}", retryRejected: false),
                cancellationToken))
            .ToArray();
        await logger.TargetReached.Task.WaitAsync(cancellationToken);
        var overflow = actor.Ask<SkillSyncResult.Response>(
            ServerFeedSkillSyncActor.Run.ForPlugin("overflow", retryRejected: false),
            cancellationToken);

        await Assert.ThrowsAsync<ServerFeedSkillSyncActor.SyncQueueFullException>(() => overflow);
        actor.Tell(PoisonPill.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(queued));
    }

    [Fact]
    public async Task Plugin_config_restart_starts_a_named_pass()
    {
        var paths = new NetclawPaths(_temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.NetclawConfigPath)!);
        File.WriteAllText(paths.NetclawConfigPath, "{}");
        var restartSignal = new DaemonRestartSignal();
        restartSignal.TakePluginStartupScope(paths.NetclawConfigPath);
        var oldHash = DaemonRestartSignal.HashConfigFile(paths.NetclawConfigPath);
        File.WriteAllText(paths.NetclawConfigPath, "{\"SkillFeeds\":{}}");
        restartSignal.RecordPluginConfigChange(
            "team-tools",
            oldHash,
            DaemonRestartSignal.HashConfigFile(paths.NetclawConfigPath)!);

        var runner = new SequencedRunner();
        CreateActor(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerFeedSkillSyncActor>.Instance, restartSignal);
        await runner.FirstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerFeedSkillSyncActor.SyncScope.Plugin, runner.Requests[0].Scope);
        Assert.Equal("team-tools", runner.Requests[0].PluginId);
        runner.ReleaseFirst.TrySetResult();
    }

    [Fact]
    public async Task Unrelated_config_change_restores_the_complete_startup_pass()
    {
        var paths = new NetclawPaths(_temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.NetclawConfigPath)!);
        File.WriteAllText(paths.NetclawConfigPath, "{}");
        var restartSignal = new DaemonRestartSignal();
        restartSignal.TakePluginStartupScope(paths.NetclawConfigPath);
        var oldHash = DaemonRestartSignal.HashConfigFile(paths.NetclawConfigPath);
        File.WriteAllText(paths.NetclawConfigPath, "{\"SkillFeeds\":{}}");
        restartSignal.RecordPluginConfigChange(
            "team-tools",
            oldHash,
            DaemonRestartSignal.HashConfigFile(paths.NetclawConfigPath)!);
        File.WriteAllText(paths.NetclawConfigPath, "{\"Daemon\":{}}");

        var runner = new SequencedRunner();
        CreateActor(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerFeedSkillSyncActor>.Instance, restartSignal);
        await runner.FirstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerFeedSkillSyncActor.SyncScope.Complete, runner.Requests[0].Scope);
        runner.ReleaseFirst.TrySetResult();
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        _temp.Dispose();
    }

    private IActorRef CreateActor(
        IServerFeedSkillSyncRunner runner,
        ILogger<ServerFeedSkillSyncActor> logger)
        => CreateActor(runner, logger, new DaemonRestartSignal());

    private IActorRef CreateActor(
        IServerFeedSkillSyncRunner runner,
        ILogger<ServerFeedSkillSyncActor> logger,
        DaemonRestartSignal restartSignal)
    {
        return _system.ActorOf(Props.Create(() => new ServerFeedSkillSyncActor(
            runner,
            new SkillFeedsConfig { SyncIntervalMinutes = 0 },
            restartSignal,
            new NetclawPaths(_temp.Path),
            logger)));
    }

    private sealed class ControlledRunner : IServerFeedSkillSyncRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _passCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PassCount => Volatile.Read(ref _passCount);
        public CancellationToken LifetimeToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<SkillSyncResult.Response> SyncAsync(
            ServerFeedSkillSyncActor.Run request,
            CancellationToken cancellationToken)
        {
            LifetimeToken = cancellationToken;
            Interlocked.Increment(ref _passCount);
            Started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }

            return new SkillSyncResult.Response
            {
                PassId = Guid.NewGuid().ToString("N"),
                Sources = [],
                Inventory = new SkillSyncResult.InventoryRow { Succeeded = true },
            };
        }
    }

    private sealed class SequencedRunner : IServerFeedSkillSyncRunner
    {
        private readonly object _gate = new();
        private readonly List<ServerFeedSkillSyncActor.Run> _requests = [];
        private int _passCount;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<bool> RetryModes
        {
            get
            {
                lock (_gate)
                    return _requests.Select(static request => request.RetryRejected).ToArray();
            }
        }

        public IReadOnlyList<ServerFeedSkillSyncActor.Run> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public async Task<SkillSyncResult.Response> SyncAsync(
            ServerFeedSkillSyncActor.Run request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                _requests.Add(request);
            var pass = Interlocked.Increment(ref _passCount);
            if (pass == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }

            return new SkillSyncResult.Response
            {
                PassId = pass.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Sources = [],
                Inventory = new SkillSyncResult.InventoryRow { Succeeded = true },
            };
        }
    }

    private sealed class JoinSignalLogger(int targetCount) : ILogger<ServerFeedSkillSyncActor>
    {
        private int _count;

        public TaskCompletionSource TargetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "Joined the active external skill sync pass."
                && Interlocked.Increment(ref _count) == targetCount)
            {
                TargetReached.TrySetResult();
            }
        }
    }

    private sealed class QueueSignalLogger(int targetCount) : ILogger<ServerFeedSkillSyncActor>
    {
        private int _count;

        public TaskCompletionSource TargetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "Queued one external skill sync pass after the active pass."
                && Interlocked.Increment(ref _count) == targetCount)
            {
                TargetReached.TrySetResult();
            }
        }
    }
}
