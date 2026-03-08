namespace Netclaw.Configuration;

/// <summary>
/// Configuration for startup synchronization of built-in system skills.
/// </summary>
public sealed class SkillSyncConfig
{
    /// <summary>
    /// When true, skip feed-based system skill sync at daemon startup and use
    /// local built-in/on-disk skills only.
    /// </summary>
    public bool DisableSystemSkillSync { get; set; } = false;
}
