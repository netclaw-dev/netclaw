namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the cross-session memory subsystem.
/// SQLite-backed durable memory settings.
/// </summary>
public sealed class MemoryConfig
{
    /// <summary>
    /// When false, the entire cross-session memory subsystem is disabled.
    /// Tools and automatic recall are not wired up regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Automatic recall timeout budget in milliseconds.
    /// </summary>
    public int RecallTimeoutMs { get; set; } = 300;

    /// <summary>
    /// Maximum number of items injected into the automatic recall bundle.
    /// </summary>
    public int AutoRecallMaxItems { get; set; } = 3;
}
