namespace Netclaw.Actors.Reminders;

/// <summary>
/// Result of resolving a user- or LLM-supplied reminder notification target.
/// Exactly one of <see cref="ResolvedChannelId"/> or <see cref="ResolvedUserId"/>
/// will be non-null on success.
/// </summary>
public sealed record ReminderTargetResolution(
    bool Success,
    string? ErrorMessage,
    string? ResolvedChannelId,
    string? ResolvedUserId);

/// <summary>
/// Resolves a user- or LLM-supplied reminder notification target
/// (channel name, user handle, or raw channel/user ID) into canonical
/// identifiers that can be persisted on a <see cref="ReminderDefinition"/>.
/// Transport-agnostic so non-Slack channels can plug in later.
/// </summary>
public interface IReminderTargetResolver
{
    Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default);
}
