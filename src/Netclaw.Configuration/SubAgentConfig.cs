namespace Netclaw.Configuration;

/// <summary>
/// Configuration for subagent timeout behavior.
/// Bound from the <c>SubAgents</c> section of <c>netclaw.json</c>.
/// All values are in seconds and must be between 5 and 600.
/// </summary>
public sealed class SubAgentConfig
{
    /// <summary>
    /// When false, the subagent subsystem is disabled.
    /// No subagent-based tools are registered regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default timeout for subagent execution when no tool-specific override exists.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Timeout for the <c>store_memory</c> curation subagent.
    /// </summary>
    public int StoreMemoryTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Timeout for the <c>search_memories</c> retrieval subagent.
    /// </summary>
    public int SearchMemoriesTimeoutSeconds { get; set; } = 30;
}
