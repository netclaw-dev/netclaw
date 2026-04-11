using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Coordinates bounded, policy-aware durable memory recall for user-facing turns.
/// </summary>
public interface IMemoryRecallCoordinator
{
    Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request for automatic pre-turn memory recall.
/// </summary>
public sealed record AutomaticRecallRequest(
    string SessionId,
    string Query,
    IReadOnlyList<string> RecentUserMessages,
    int MaxItems,
    TrustAudience Audience = TrustAudience.Public,
    string? Boundary = null,
    IReadOnlyList<string>? RecentAssistantMessages = null,
    IReadOnlyList<string>? RecentEntities = null,
    string? ThreadTitle = null);

/// <summary>
/// Automatic recall output for a single turn.
/// </summary>
public sealed record AutomaticRecallResult(
    IReadOnlyList<AutomaticRecallItem> Items,
    bool Degraded = false,
    string? DegradeReason = null,
    string? DegradeStage = null);

/// <summary>
/// A single memory item selected for automatic recall.
/// </summary>
public sealed record AutomaticRecallItem(
    string Id,
    string Title,
    string Content,
    string Sensitivity,
    double Score);

/// <summary>
/// No-op automatic recall coordinator used when recall is not configured.
/// </summary>
public sealed class NullMemoryRecallCoordinator : IMemoryRecallCoordinator
{
    public static readonly NullMemoryRecallCoordinator Instance = new();

    public Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
        => Task.FromResult(new AutomaticRecallResult([]));
}
