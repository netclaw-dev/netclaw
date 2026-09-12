// -----------------------------------------------------------------------
// <copyright file="DailyStatsPublisher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Telemetry;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// <see cref="ISessionMetrics"/> implementation that pushes deltas to
/// OTel counters (<see cref="SessionTelemetry"/>) and the
/// <see cref="DailyStatsActor"/> for persistent daily aggregation
/// and process-lifetime accumulation.
/// </summary>
public sealed class DailyStatsPublisher : ISessionMetrics
{
    private readonly IRequiredActor<DailyStatsActorKey> _actorProvider;
    private IActorRef? _statsActor;

    public DailyStatsPublisher(IRequiredActor<DailyStatsActorKey> actorProvider)
    {
        _actorProvider = actorProvider;
    }

    public void RecordTokenUsage(long inputTokens, long outputTokens)
    {
        SessionTelemetry.RecordUsage(inputTokens, outputTokens);
        GetActor()?.Tell(new DailyStatsActor.RecordTokenUsage(inputTokens, outputTokens));
    }

    public void RecordTurnCompleted()
    {
        SessionTelemetry.RecordTurnCompleted();
        GetActor()?.Tell(new DailyStatsActor.RecordTurnCompleted());
    }

    public void RecordSessionCreated()
    {
        GetActor()?.Tell(new DailyStatsActor.RecordSessionCreated());
    }

    public void RecordMemoriesFormed(int count)
    {
        GetActor()?.Tell(new DailyStatsActor.RecordMemoriesFormed(count));
    }

    public void RecordMemoriesRecalled(int count)
    {
        GetActor()?.Tell(new DailyStatsActor.RecordMemoriesRecalled(count));
    }

    public void RecordSkillsLoaded(int count)
    {
        GetActor()?.Tell(new DailyStatsActor.RecordSkillsLoaded(count));
    }

    public void RecordSkillLoaded(string skillName, SkillLoadMethod method)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return;

        GetActor()?.Tell(new DailyStatsActor.RecordSkillLoaded(skillName.Trim(), method));
    }

    /// <summary>
    /// Lazily resolves the actor ref on first use. Returns null if the
    /// actor system hasn't finished starting yet (messages are silently dropped).
    /// </summary>
    private IActorRef? GetActor()
    {
        if (_statsActor is not null)
            return _statsActor;

        // TryGetAsync would be ideal but ActorRef is the synchronous path.
        // Use the non-blocking check: if the actor is registered, cache it.
        try
        {
            _statsActor = _actorProvider.ActorRef;
            return _statsActor;
        }
        catch
        {
            // Actor not yet registered — drop the message silently.
            // Next call will retry.
            return null;
        }
    }
}
