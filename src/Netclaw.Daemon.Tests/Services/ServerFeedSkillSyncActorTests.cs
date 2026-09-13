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
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncActorTests : IDisposable
{
    private readonly ActorSystem _system = ActorSystem.Create($"skill-sync-tests-{Guid.NewGuid():N}");

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

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
    }

    private IActorRef CreateActor(
        IServerFeedSkillSyncRunner runner,
        ILogger<ServerFeedSkillSyncActor> logger)
    {
        return _system.ActorOf(Props.Create(() => new ServerFeedSkillSyncActor(
            runner,
            new SkillFeedsConfig { SyncIntervalMinutes = 0 },
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

        public async Task<SkillSyncResult.Response> SyncAsync(CancellationToken cancellationToken)
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
}
