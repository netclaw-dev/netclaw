using System.Text.Json;

namespace Netclaw.Actors.Memory;

public sealed record MemoryCheckpointRequest(
    string SessionId,
    string? TurnId,
    string TriggerType,
    int Priority,
    object Payload);

public sealed record MemoryCheckpointEnqueueResult(
    string CheckpointId,
    long EnqueuedAtMs);

public interface IMemoryCheckpointSink
{
    Task<MemoryCheckpointEnqueueResult> EnqueueAsync(MemoryCheckpointRequest request, CancellationToken ct = default);
}

public sealed class NullMemoryCheckpointSink : IMemoryCheckpointSink
{
    public static readonly NullMemoryCheckpointSink Instance = new();

    public Task<MemoryCheckpointEnqueueResult> EnqueueAsync(MemoryCheckpointRequest request, CancellationToken ct = default)
    {
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        return Task.FromResult(new MemoryCheckpointEnqueueResult($"noop-{Guid.NewGuid():N}", now));
    }
}

public sealed class SQLiteMemoryCheckpointSink(
    SQLiteMemoryStore store,
    TimeProvider timeProvider) : IMemoryCheckpointSink
{
    public async Task<MemoryCheckpointEnqueueResult> EnqueueAsync(MemoryCheckpointRequest request, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var checkpointId = $"cp-{Guid.NewGuid():N}";

        var payloadJson = request.Payload is string s
            ? s
            : JsonSerializer.Serialize(request.Payload);

        await store.EnqueueCheckpointAsync(new SQLiteMemoryCheckpoint(
            CheckpointId: checkpointId,
            SessionId: request.SessionId,
            TurnId: request.TurnId,
            TriggerType: request.TriggerType,
            Priority: request.Priority,
            Status: "pending",
            PayloadJson: payloadJson,
            RetryCount: 0,
            CreatedAtMs: now,
            UpdatedAtMs: now), ct);

        return new MemoryCheckpointEnqueueResult(checkpointId, now);
    }
}
