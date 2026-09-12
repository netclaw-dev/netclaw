// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

internal sealed class ServerFeedSkillSyncActorKey;

internal sealed class ServerFeedSkillSyncActor : ReceiveActor, IWithTimers
{
    private const string PeriodicTimerKey = "server-feed-skill-sync";
    private static readonly TimeSpan MaximumInitialJitter = TimeSpan.FromMinutes(5);

    private readonly IServerFeedSkillSyncRunner _runner;
    private readonly ILogger<ServerFeedSkillSyncActor> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialJitter;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<IActorRef> _waiters = [];
    private bool _passActive;

    public ServerFeedSkillSyncActor(
        IServerFeedSkillSyncRunner runner,
        SkillFeedsConfig feedsConfig,
        ILogger<ServerFeedSkillSyncActor> logger)
    {
        _runner = runner;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(feedsConfig.SyncIntervalMinutes);
        _initialJitter = CreateInitialJitter();

        Receive<Run>(_ => HandleRequest(Sender));
        Receive<ScheduledRun>(_ => HandleScheduledRun());
        Receive<SyncPassCompleted>(completed => CompletePass(completed.Result));
        Receive<SyncPassFailed>(failed => FailPass(failed.Cause));
    }

    public ITimerScheduler Timers { get; set; } = null!;

    protected override void PreStart()
    {
        base.PreStart();
        Self.Tell(ScheduledRun.Instance);

        if (_interval <= TimeSpan.Zero)
        {
            _logger.LogInformation("Periodic server feed sync disabled (SyncIntervalMinutes=0)");
            return;
        }

        var firstDelay = _interval + _initialJitter;
        _logger.LogInformation(
            "Server feed periodic sync scheduled every {IntervalMinutes}m (first check in {FirstDelayMinutes:F1}m)",
            _interval.TotalMinutes,
            firstDelay.TotalMinutes);
        Timers.StartPeriodicTimer(
            PeriodicTimerKey,
            ScheduledRun.Instance,
            firstDelay,
            _interval);
    }

    protected override void PostStop()
    {
        var unavailable = new Status.Failure(
            new OperationCanceledException("The daemon stopped the active skill sync pass."));
        foreach (var waiter in _waiters)
            waiter.Tell(unavailable);
        _waiters.Clear();

        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        base.PostStop();
    }

    private void HandleRequest(IActorRef replyTo)
    {
        _waiters.Add(replyTo);
        if (_passActive)
        {
            _logger.LogDebug("Joined the active external skill sync pass.");
            return;
        }

        StartPass();
    }

    private void HandleScheduledRun()
    {
        if (_passActive)
        {
            _logger.LogDebug("Skipped a scheduled external skill sync because a pass is active.");
            return;
        }

        StartPass();
    }

    private void StartPass()
    {
        _passActive = true;
        _runner.SyncAsync(_lifetimeCancellation.Token).PipeTo(
            Self,
            success: result => new SyncPassCompleted(result),
            failure: cause => new SyncPassFailed(cause));
    }

    private void CompletePass(SkillSyncResult.Response result)
    {
        foreach (var waiter in _waiters)
            waiter.Tell(result);
        _waiters.Clear();
        _passActive = false;
    }

    private void FailPass(Exception cause)
    {
        _logger.LogError(cause, "External skill sync pass failed.");
        var failure = new Status.Failure(cause);
        foreach (var waiter in _waiters)
            waiter.Tell(failure);
        _waiters.Clear();
        _passActive = false;
    }

    private static TimeSpan CreateInitialJitter()
        => TimeSpan.FromSeconds(Random.Shared.Next(0, (int)MaximumInitialJitter.TotalSeconds));

    internal sealed class Run : INoSerializationVerificationNeeded
    {
        public static Run Instance { get; } = new();

        private Run()
        {
        }
    }

    private sealed class ScheduledRun : INoSerializationVerificationNeeded
    {
        public static ScheduledRun Instance { get; } = new();

        private ScheduledRun()
        {
        }
    }

    private sealed record SyncPassCompleted(SkillSyncResult.Response Result)
        : INoSerializationVerificationNeeded;

    private sealed record SyncPassFailed(Exception Cause) : INoSerializationVerificationNeeded;
}

internal static class ServerFeedSkillSyncActorHostingExtensions
{
    public static AkkaConfigurationBuilder WithServerFeedSkillSyncActor(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(
                resolver.Props<ServerFeedSkillSyncActor>(),
                "server-feed-skill-sync");
            registry.Register<ServerFeedSkillSyncActorKey>(actor);
        });
    }
}
