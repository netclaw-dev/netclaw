namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Compile-time constants for the Netclaw feed infrastructure.
/// Feed URLs are not user-configurable in MVP — they point to the
/// project-owned CDN at feeds.netclaw.dev.
/// </summary>
public static class FeedConstants
{
    /// <summary>
    /// Base URL for the Netclaw feeds CDN (Cloudflare Pages).
    /// </summary>
    public const string FeedBaseUrl = "https://feeds.netclaw.dev";

    /// <summary>
    /// URL for the system skills manifest.
    /// </summary>
    public const string SystemSkillsManifestUrl =
        $"{FeedBaseUrl}/skills/.system/manifest.json";

    /// <summary>
    /// HTTP timeout for feed manifest and skill downloads.
    /// Short timeout ensures startup is never blocked by network issues.
    /// </summary>
    public static readonly TimeSpan FeedHttpTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Name of the sync state file written inside the system skills directory.
    /// </summary>
    public const string SyncStateFileName = ".sync-state.json";
}
