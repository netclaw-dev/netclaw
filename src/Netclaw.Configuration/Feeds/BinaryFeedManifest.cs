// -----------------------------------------------------------------------
// <copyright file="BinaryFeedManifest.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;

namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Wire type for the binary releases feed manifest (releases/manifest.json).
/// Deserialized from the CDN to check for available updates.
/// </summary>
public sealed class BinaryFeedManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("feedType")]
    public string FeedType { get; init; } = "releases";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("latest")]
    public string Latest { get; init; } = "";

    /// <summary>
    /// Newest version of any kind — stable or prerelease (always &gt;= <see cref="Latest"/>).
    /// What the beta channel resolves to. Empty on manifests published before the
    /// prerelease channel existed; stable clients never read this field.
    /// </summary>
    [JsonPropertyName("latestPrerelease")]
    public string LatestPrerelease { get; init; } = "";

    [JsonPropertyName("releases")]
    public List<BinaryRelease> Releases { get; init; } = [];
}

/// <summary>
/// A single release in the binary feed manifest.
/// </summary>
public sealed class BinaryRelease
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("releasedAt")]
    public DateTimeOffset ReleasedAt { get; init; }

    [JsonPropertyName("releaseNotesUrl")]
    public string? ReleaseNotesUrl { get; init; }

    [JsonPropertyName("assets")]
    public List<BinaryAsset> Assets { get; init; } = [];
}

/// <summary>
/// A single downloadable binary asset (component + platform combination).
/// </summary>
public sealed class BinaryAsset
{
    [JsonPropertyName("component")]
    public required string Component { get; init; }

    [JsonPropertyName("rid")]
    public required string Rid { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }
}
