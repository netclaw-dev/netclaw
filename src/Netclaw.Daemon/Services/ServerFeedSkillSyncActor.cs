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
    private const int MaximumQueuedPasses = 2 * ManagedPluginSourceValidator.MaximumSourceCount + 4;
    private static readonly TimeSpan MaximumInitialJitter = TimeSpan.FromMinutes(5);

    private readonly IServerFeedSkillSyncRunner _runner;
    private readonly ILogger<ServerFeedSkillSyncActor> _logger;
    private readonly DaemonRestartSignal _restartSignal;
    private readonly NetclawPaths _paths;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialJitter;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<IActorRef> _activeWaiters = [];
    private readonly Queue<PendingPass> _queuedPasses = new();
    private Run? _activeRequest;

    public ServerFeedSkillSyncActor(
        IServerFeedSkillSyncRunner runner,
        SkillFeedsConfig feedsConfig,
        DaemonRestartSignal restartSignal,
        NetclawPaths paths,
        ILogger<ServerFeedSkillSyncActor> logger)
    {
        _runner = runner;
        _logger = logger;
        _restartSignal = restartSignal;
        _paths = paths;
        _interval = TimeSpan.FromMinutes(feedsConfig.SyncIntervalMinutes);
        _initialJitter = CreateInitialJitter();

        Receive<Run>(request => HandleRequest(request, Sender));
        Receive<ScheduledRun>(scheduled => HandleScheduledRun(scheduled.Request));
        Receive<SyncPassCompleted>(completed => CompletePass(completed.Result));
        Receive<SyncPassFailed>(failed => FailPass(failed.Cause));
    }

    public ITimerScheduler Timers { get; set; } = null!;

    protected override void PreStart()
    {
        base.PreStart();
        var (pluginIds, invalidScope) = _restartSignal.TakePluginStartupScope(_paths.NetclawConfigPath);
        if (invalidScope)
            _logger.LogWarning("Plugin config changed outside the recorded mutation. Startup will sync all sources.");
        var startupRequest = pluginIds?.Count switch
        {
            1 => Run.ForPlugin(pluginIds[0], retryRejected: false),
            > 1 => Run.ForPlugins(retryRejected: false),
            _ => Run.Instance,
        };
        Self.Tell(new ScheduledRun(startupRequest));

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
        foreach (var waiter in _activeWaiters.Concat(_queuedPasses.SelectMany(static pass => pass.Waiters)))
            waiter.Tell(unavailable);
        _activeWaiters.Clear();
        _queuedPasses.Clear();

        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        base.PostStop();
    }

    private void HandleRequest(Run request, IActorRef replyTo)
    {
        if (_activeRequest is null)
        {
            _activeWaiters.Add(replyTo);
            StartPass(request);
            return;
        }

        if (_activeRequest == request)
        {
            _activeWaiters.Add(replyTo);
            _logger.LogDebug("Joined the active external skill sync pass.");
            return;
        }

        var queued = _queuedPasses.FirstOrDefault(pass => pass.Request == request);
        if (queued is null)
        {
            if (_queuedPasses.Count >= MaximumQueuedPasses)
            {
                replyTo.Tell(new Status.Failure(new SyncQueueFullException()));
                return;
            }

            queued = new PendingPass(request);
            _queuedPasses.Enqueue(queued);
        }

        queued.Waiters.Add(replyTo);
        _logger.LogDebug("Queued one external skill sync pass after the active pass.");
    }

    private void HandleScheduledRun(Run request)
    {
        if (_activeRequest is not null)
        {
            _logger.LogDebug("Skipped a scheduled external skill sync because a pass is active.");
            return;
        }

        StartPass(request);
    }

    private void StartPass(Run request)
    {
        _activeRequest = request;
        _runner.SyncAsync(request, _lifetimeCancellation.Token).PipeTo(
            Self,
            success: result => new SyncPassCompleted(result),
            failure: cause => new SyncPassFailed(cause));
    }

    private void CompletePass(SkillSyncResult.Response result)
    {
        foreach (var waiter in _activeWaiters)
            waiter.Tell(result);
        _activeWaiters.Clear();
        StartQueuedPassOrStop();
    }

    private void FailPass(Exception cause)
    {
        _logger.LogError(cause, "External skill sync pass failed.");
        var failure = new Status.Failure(cause);
        foreach (var waiter in _activeWaiters)
            waiter.Tell(failure);
        _activeWaiters.Clear();
        StartQueuedPassOrStop();
    }

    private void StartQueuedPassOrStop()
    {
        if (_queuedPasses.Count == 0)
        {
            _activeRequest = null;
            return;
        }

        var queued = _queuedPasses.Dequeue();
        _activeWaiters.AddRange(queued.Waiters);
        StartPass(queued.Request);
    }

    private static TimeSpan CreateInitialJitter()
        => TimeSpan.FromSeconds(Random.Shared.Next(0, (int)MaximumInitialJitter.TotalSeconds));

    internal enum SyncScope
    {
        Complete,
        Plugins,
        Plugin,
    }

    internal sealed class SyncQueueFullException : Exception
    {
        public SyncQueueFullException()
            : base("The skill sync queue is full. Retry after the active pass ends.")
        {
        }
    }

    internal sealed record Run : INoSerializationVerificationNeeded
    {
        private Run(SyncScope scope, string? pluginId, bool retryRejected)
        {
            Scope = scope;
            PluginId = pluginId;
            RetryRejected = retryRejected;
        }

        public SyncScope Scope { get; }
        public string? PluginId { get; }
        public bool RetryRejected { get; }

        public static Run Instance { get; } = new(SyncScope.Complete, null, false);
        public static Run RetryRejectedCommits { get; } = new(SyncScope.Complete, null, true);

        public static Run ForPlugins(bool retryRejected)
            => new(SyncScope.Plugins, null, retryRejected);

        public static Run ForPlugin(string pluginId, bool retryRejected)
            => new(SyncScope.Plugin, pluginId, retryRejected);
    }

    private sealed record PendingPass(Run Request)
    {
        public List<IActorRef> Waiters { get; } = [];
    }

    private sealed record ScheduledRun(Run Request) : INoSerializationVerificationNeeded
    {
        public static ScheduledRun Instance { get; } = new(Run.Instance);
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
