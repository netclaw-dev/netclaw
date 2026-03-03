using System.Text.Json.Serialization;

namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Wire type for the system skills feed manifest (manifest.json).
/// Deserialized from the CDN at startup.
/// </summary>
public sealed class SkillFeedManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("feedType")]
    public string FeedType { get; init; } = "system";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("skills")]
    public List<SkillFeedEntry> Skills { get; init; } = [];
}

/// <summary>
/// A single skill entry in the feed manifest.
/// </summary>
public sealed class SkillFeedEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("minimumDaemonVersion")]
    public string? MinimumDaemonVersion { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}
