// -----------------------------------------------------------------------
// <copyright file="FeedConstants.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Compile-time constants for the Netclaw feed infrastructure.
/// Feed URLs are not user-configurable in MVP.
/// </summary>
public static class FeedConstants
{
    /// <summary>
    /// Base URL for the system skills feed (Cloudflare Pages).
    /// </summary>
    public const string FeedBaseUrl = "https://feeds.netclaw.dev";

    /// <summary>
    /// URL for the system skills manifest.
    /// </summary>
    public const string SystemSkillsManifestUrl =
        $"{FeedBaseUrl}/skills/.system/manifest.json";

    /// <summary>
    /// URL for the binary releases manifest.
    /// Binary assets are hosted on the dedicated releases domain, not the skills feed domain.
    /// </summary>
    public const string BinaryManifestUrl = "https://releases.netclaw.dev/manifest.json";

    /// <summary>
    /// URL for the binary releases manifest signature (minisign detached signature).
    /// </summary>
    public const string BinaryManifestSignatureUrl = "https://releases.netclaw.dev/manifest.json.sig";

    /// <summary>
    /// Base URL for skill file downloads (Cloudflare R2 + custom domain).
    /// Skill files are immutable and cached indefinitely at versioned URLs.
    /// The manifest URL remains at <see cref="FeedBaseUrl"/> on Cloudflare Pages.
    /// </summary>
    public const string SkillFilesBaseUrl = "https://skills.netclaw.dev";

    /// <summary>
    /// HTTP timeout for feed manifest and skill downloads.
    /// Short timeout ensures startup is never blocked by network issues.
    /// </summary>
    public static readonly TimeSpan FeedHttpTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// HTTP timeout for binary feed manifest fetches.
    /// Slightly longer than skill feed since binary manifests may be larger.
    /// </summary>
    public static readonly TimeSpan BinaryFeedHttpTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Name of the sync state file written inside the system skills directory.
    /// </summary>
    public const string SyncStateFileName = ".sync-state.json";
}
