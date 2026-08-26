// -----------------------------------------------------------------------
// <copyright file="SkillFeedsConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for private skill server feeds. Organizations can host
/// skill-server instances and have Netclaw daemons automatically discover
/// and sync skills at startup and periodically thereafter.
/// </summary>
public sealed class SkillFeedsConfig
{
    /// <summary>
    /// Ordered list of skill server feeds. Precedence follows list order —
    /// earlier feeds win on name collisions. Native Netclaw skills and
    /// system skills always take highest precedence regardless of order.
    /// </summary>
    public List<SkillFeedSource> Feeds { get; set; } = [];

    /// <summary>
    /// How often (in minutes) to re-check feeds for updated skills.
    /// Default: 60 (once per hour). Set to 0 to disable periodic sync
    /// and only sync at daemon startup.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// A single skill server feed source.
/// </summary>
public sealed class SkillFeedSource
{
    /// <summary>
    /// Unique identifier for this feed (used as directory name and display label).
    /// Must be filesystem-safe: lowercase alphanumeric and hyphens.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Base URL of the skill server (e.g., "https://skills.corp.com").
    /// The daemon appends <c>/.well-known/agent-skills/index.json</c> for RFC discovery.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Optional API key for authenticated access to the skill server.
    /// Supports <c>ENC:</c> prefix for encrypted storage in secrets.json.
    /// </summary>
    public SensitiveString? ApiKey { get; set; }

    /// <summary>
    /// Whether this feed is active. Default: <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// HTTP timeout in seconds for requests to this feed. Default: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
