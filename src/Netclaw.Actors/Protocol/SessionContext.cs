namespace Netclaw.Actors.Protocol;

/// <summary>
/// Installs additive session-scoped prompt context without replacing the base
/// system prompt assembled from identity files.
/// </summary>
public sealed record SetSessionPromptOverlay : IWithSessionId
{
    public required SessionId SessionId { get; init; }

    public string PromptOverlay { get; init; } = string.Empty;
}
