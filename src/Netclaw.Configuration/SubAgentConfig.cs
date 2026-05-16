// -----------------------------------------------------------------------
// <copyright file="SubAgentConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for subagent timeout behavior.
/// Bound from the <c>SubAgents</c> section of <c>netclaw.json</c>.
/// All values are in seconds.
/// </summary>
public sealed class SubAgentConfig
{
    /// <summary>
    /// When false, the subagent subsystem is disabled.
    /// No subagent-based tools are registered regardless of audience profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default absolute wall-clock backstop for a sub-agent run. A sub-agent's
    /// primary control is an inactivity watchdog; this backstop only bounds a run
    /// that keeps producing activity but never finishes, so it is generous.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = SubAgentProfile.DefaultBackstopSeconds;

    /// <summary>
    /// Timeout for the <c>store_memory</c> curation subagent.
    /// </summary>
    public int StoreMemoryTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Timeout for the <c>search_memories</c> retrieval subagent.
    /// </summary>
    public int SearchMemoriesTimeoutSeconds { get; set; } = 30;
}
