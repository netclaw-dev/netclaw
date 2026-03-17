using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Hosting;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Thin fire-and-forget wrapper over <see cref="DailyStatsActor"/>.
/// Same API shape as the old <c>DailyStatsRecorder</c> so call sites barely change.
/// </summary>
public sealed class DailyStatsPublisher
{
    private readonly IRequiredActor<DailyStatsActorKey> _actorProvider;

    public DailyStatsPublisher(IRequiredActor<DailyStatsActorKey> actorProvider)
    {
        _actorProvider = actorProvider;
    }

    public void RecordTokenUsage(long inputTokens, long outputTokens)
        => Tell(new DailyStatsActor.RecordTokenUsage(inputTokens, outputTokens));

    public void RecordTurnCompleted()
        => Tell(new DailyStatsActor.RecordTurnCompleted());

    public void RecordSessionCreated()
        => Tell(new DailyStatsActor.RecordSessionCreated());

    public void RecordMemoriesFormed(int count)
        => Tell(new DailyStatsActor.RecordMemoriesFormed(count));

    public void RecordMemoriesRecalled(int count)
        => Tell(new DailyStatsActor.RecordMemoriesRecalled(count));

    public void RecordSkillsLoaded(int count)
        => Tell(new DailyStatsActor.RecordSkillsLoaded(count));

    private void Tell(object message)
    {
        // GetAsync completes synchronously after actor startup; safe to .Result here
        // since the actor is registered before any callers are active.
        var actor = _actorProvider.ActorRef;
        actor.Tell(message);
    }
}
