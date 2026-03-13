namespace Netclaw.Actors.Memory;

public sealed record ObservedMemoryCheckpointPayload(
    string SessionId,
    string TriggerType,
    string Domain,
    string Sensitivity,
    IReadOnlyList<SQLiteMemoryCurationOperation> Operations);
