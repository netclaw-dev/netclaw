namespace Netclaw.Actors.Reminders;

/// <summary>
/// Result of resolving a user- or LLM-supplied reminder notification target.
/// On success, <see cref="ResolvedId"/> holds the canonical routing identifier
/// (channel or user) for the target transport.
/// </summary>
public sealed record ReminderTargetResolution(
    bool Success,
    string? ResolvedId,
    string? ErrorMessage);

/// <summary>
/// Resolves a user- or LLM-supplied reminder notification target
/// (channel name, user handle, or raw channel/user ID) into a canonical
/// identifier that can be persisted on a <see cref="ReminderDefinition"/>.
/// Transport-agnostic so non-Slack channels can plug in later.
/// </summary>
public interface IReminderTargetResolver
{
    Task<ReminderTargetResolution> ResolveAsync(string target, CancellationToken ct = default);
}
