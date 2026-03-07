namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the cross-session memory subsystem.
/// SQLite-first durable memory settings.
/// </summary>
public sealed class MemoryConfig
{
    /// <summary>
    /// Durability backend. MVP defaults to local SQLite.
    /// </summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>
    /// Automatic recall timeout budget in milliseconds.
    /// </summary>
    public int RecallTimeoutMs { get; set; } = 300;

    /// <summary>
    /// Maximum number of items injected into the automatic recall bundle.
    /// </summary>
    public int AutoRecallMaxItems { get; set; } = 3;
}
