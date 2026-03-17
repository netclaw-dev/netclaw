using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Telemetry;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// <see cref="ISessionMetrics"/> implementation that pushes deltas to
/// OTel counters (<see cref="SessionTelemetry"/>) and the
/// <see cref="DailyStatsActor"/> for persistent daily aggregation
/// and process-lifetime accumulation.
/// </summary>
public sealed class DailyStatsPublisher : ISessionMetrics
{
    private readonly IActorRef _statsActor;

    public DailyStatsPublisher(IRequiredActor<DailyStatsActorKey> actorProvider)
    {
        _statsActor = actorProvider.ActorRef;
    }

    public void RecordTokenUsage(long inputTokens, long outputTokens)
    {
        SessionTelemetry.RecordUsage(inputTokens, outputTokens);
        _statsActor.Tell(new DailyStatsActor.RecordTokenUsage(inputTokens, outputTokens));
    }

    public void RecordTurnCompleted()
    {
        SessionTelemetry.RecordTurnCompleted();
        _statsActor.Tell(new DailyStatsActor.RecordTurnCompleted());
    }

    public void RecordSessionCreated()
    {
        _statsActor.Tell(new DailyStatsActor.RecordSessionCreated());
    }

    public void RecordMemoriesFormed(int count)
    {
        _statsActor.Tell(new DailyStatsActor.RecordMemoriesFormed(count));
    }

    public void RecordMemoriesRecalled(int count)
    {
        _statsActor.Tell(new DailyStatsActor.RecordMemoriesRecalled(count));
    }

    public void RecordSkillsLoaded(int count)
    {
        _statsActor.Tell(new DailyStatsActor.RecordSkillsLoaded(count));
    }
}
