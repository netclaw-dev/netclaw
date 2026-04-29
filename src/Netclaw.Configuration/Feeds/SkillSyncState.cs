// -----------------------------------------------------------------------
// <copyright file="SkillSyncState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Tracks which system skills have been synced from the feed.
/// Persisted at <c>~/.netclaw/skills/.system/.sync-state.json</c>.
/// </summary>
public sealed class SkillSyncState
{
    [JsonPropertyName("lastSyncUtc")]
    public DateTimeOffset LastSyncUtc { get; set; }

    [JsonPropertyName("skills")]
    public Dictionary<string, SyncedSkillState> Skills { get; set; } = [];
}

/// <summary>
/// Per-skill sync tracking within <see cref="SkillSyncState"/>.
/// </summary>
public sealed class SyncedSkillState
{
    [JsonPropertyName("version")]
    public required string Version { get; set; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; set; }

    [JsonPropertyName("syncedAtUtc")]
    public DateTimeOffset SyncedAtUtc { get; set; }
}
