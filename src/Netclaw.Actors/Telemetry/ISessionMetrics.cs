namespace Netclaw.Actors.Telemetry;

/// <summary>
/// Lightweight sidecar for recording session-level usage metrics.
/// Injected into actors and services that produce telemetry events.
/// Implementations are fire-and-forget — callers never block on persistence.
/// </summary>
public interface ISessionMetrics
{
    void RecordTokenUsage(long inputTokens, long outputTokens);
    void RecordTurnCompleted();
    void RecordSessionCreated();
    void RecordMemoriesFormed(int count);
    void RecordMemoriesRecalled(int count);
    void RecordSkillsLoaded(int count);
}
