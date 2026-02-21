namespace Akka.Agents.Sessions;

public sealed record SessionConfig(
    int SnapshotInterval,
    int CompactionThreshold,
    int MaxHistoryMessages);
