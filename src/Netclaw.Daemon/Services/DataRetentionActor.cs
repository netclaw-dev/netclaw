// -----------------------------------------------------------------------
// <copyright file="DataRetentionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// One kind of data that expires. <see cref="Prune"/> receives the current time (the job already
/// knows its <see cref="Days"/>) and returns how many items it deleted and how many it could not.
/// Another kind of data joins the schedule by registering one more <see cref="RetentionJob"/>
/// in DI.
/// </summary>
/// <param name="Name">What the job prunes, as it reads in a log line.</param>
/// <param name="Days">How long data is kept; zero or less keeps it forever.</param>
/// <param name="Prune">Deletes the expired items.</param>
internal sealed record RetentionJob(string Name, int Days, Func<DateTimeOffset, (int Deleted, int Failed)> Prune);

internal sealed class DataRetentionActorKey;

/// <summary>
/// Runs every registered <see cref="RetentionJob"/> shortly after the daemon starts and then
/// every <see cref="Interval"/>, so a daemon that stays up for months still clears expired data.
/// A job that fails is logged and does not stop the other jobs or the schedule.
/// </summary>
internal sealed class DataRetentionActor : ReceiveActor, IWithTimers
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private const string TimerKey = "data-retention";

    private readonly IReadOnlyList<RetentionJob> _jobs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DataRetentionActor> _logger;

    public DataRetentionActor(
        IEnumerable<RetentionJob> jobs,
        TimeProvider timeProvider,
        ILogger<DataRetentionActor> logger)
    {
        _jobs = jobs.ToArray();
        _timeProvider = timeProvider;
        _logger = logger;

        Receive<RunRetention>(_ => RunJobs());
    }

    public ITimerScheduler Timers { get; set; } = null!;

    protected override void PreStart()
    {
        base.PreStart();
        Timers.StartPeriodicTimer(TimerKey, RunRetention.Instance, InitialDelay, Interval);
    }

    private void RunJobs()
    {
        foreach (var job in _jobs)
        {
            if (job.Days <= 0)
                continue;

            try
            {
                var (deleted, failed) = job.Prune(_timeProvider.GetUtcNow());
                if (deleted > 0)
                    _logger.LogInformation("Retention: deleted {Deleted} expired {Name} file(s) older than {Days} days.", deleted, job.Name, job.Days);
                if (failed > 0)
                    _logger.LogWarning("Retention: could not delete {Failed} expired {Name} file(s); will retry at the next run.", failed, job.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention job {Name} failed; the other jobs still run.", job.Name);
            }
        }
    }

    internal sealed class RunRetention : INoSerializationVerificationNeeded
    {
        public static RunRetention Instance { get; } = new();

        private RunRetention()
        {
        }
    }
}

internal static class DataRetentionActorHostingExtensions
{
    /// <summary>
    /// Registers every <see cref="RetentionJob"/> the daemon runs. Another kind of data joins the
    /// schedule by adding its job here and its warning, if any, to the list. The returned warnings
    /// are configuration warnings for the caller to log once logging is up.
    /// </summary>
    public static IReadOnlyList<string> AddRetentionJobs(
        this IServiceCollection services,
        IConfiguration configuration,
        NetclawPaths paths)
    {
        var warnings = new List<string>();

        services.AddSingleton(DaemonLogRetention.CreateJob(configuration, paths, out var logsWarning));
        if (logsWarning is not null)
            warnings.Add(logsWarning);

        return warnings;
    }

    /// <summary>
    /// Starts the retention actor. It runs the <see cref="RetentionJob"/> instances registered in DI.
    /// </summary>
    public static AkkaConfigurationBuilder WithDataRetentionActor(this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(
                resolver.Props<DataRetentionActor>(),
                "data-retention");
            registry.Register<DataRetentionActorKey>(actor);
        });
    }
}
