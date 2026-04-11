namespace Netclaw.Configuration;

/// <summary>
/// Coarse policy classes for inbound file attachments. Channel adapters map
/// MIME types to these classes via <see cref="AttachmentCategories.FromMime"/>,
/// and per-audience policy allows or denies each class. Categories exist so
/// operators can reason about attachment trust in human-friendly terms
/// ("allow images") rather than maintaining MIME-type allowlists.
/// Unknown or unrecognized MIME types map to <see cref="Other"/> and are
/// fail-closed at all audiences except <c>Personal</c> by default.
/// </summary>
public enum AttachmentCategory
{
    Image,
    Pdf,
    Document,
    Archive,
    Media,
    Other
}

/// <summary>
/// Per-audience policy for inbound channel attachments. Channel adapters query
/// this from the resolved <see cref="ToolAudienceProfile"/> before building
/// <c>ChannelInput.Contents</c> for an inbound message. The empty policy
/// (<see cref="Empty"/>) denies every category and is the fail-closed default.
/// </summary>
public sealed class ChannelAttachmentPolicy
{
    public const long DefaultMaxFileBytes = 25L * 1024 * 1024;
    public const int DefaultMaxFilesPerMessage = 10;

    /// <summary>
    /// Categories allowed for this audience. Empty means all attachments are
    /// rejected regardless of category.
    /// </summary>
    public List<AttachmentCategory> AllowedCategories { get; set; } = [];

    /// <summary>
    /// Maximum per-file byte size. Files whose transport-reported size exceeds
    /// this are rejected before download.
    /// </summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>
    /// Maximum number of attached files accepted on a single inbound message.
    /// Inbound messages exceeding this are rejected with a user-visible reply.
    /// </summary>
    public int MaxFilesPerMessage { get; set; } = DefaultMaxFilesPerMessage;

    /// <summary>
    /// Fail-closed policy: no categories permitted, zero size, zero file
    /// count. Used as the default value for <see cref="ToolAudienceProfile.ChannelAttachments"/>
    /// so an unconfigured profile rejects every attachment until operators
    /// opt in.
    /// </summary>
    public static ChannelAttachmentPolicy Empty => new()
    {
        AllowedCategories = [],
        MaxFileBytes = 0,
        MaxFilesPerMessage = 0
    };

    public bool Allows(AttachmentCategory category) => AllowedCategories.Contains(category);
}

/// <summary>
/// Maps MIME types to <see cref="AttachmentCategory"/>. This is the only
/// place in the codebase that classifies MIME strings; callers that need to
/// reason about attachment classes SHALL use <see cref="FromMime"/> rather
/// than open-coding prefix checks.
/// </summary>
public static class AttachmentCategories
{
    public static AttachmentCategory FromMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return AttachmentCategory.Other;

        var trimmed = mime.Trim();
        var semicolon = trimmed.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0)
            trimmed = trimmed[..semicolon].Trim();

        if (trimmed.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return AttachmentCategory.Image;

        if (string.Equals(trimmed, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return AttachmentCategory.Pdf;

        if (trimmed.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return AttachmentCategory.Media;

        if (IsArchive(trimmed))
            return AttachmentCategory.Archive;

        if (IsDocument(trimmed))
            return AttachmentCategory.Document;

        return AttachmentCategory.Other;
    }

    private static bool IsArchive(string mime) => mime.ToLowerInvariant() switch
    {
        "application/zip" => true,
        "application/x-zip-compressed" => true,
        "application/x-tar" => true,
        "application/gzip" => true,
        "application/x-gzip" => true,
        "application/x-7z-compressed" => true,
        "application/x-rar-compressed" => true,
        "application/vnd.rar" => true,
        "application/x-bzip2" => true,
        "application/x-xz" => true,
        _ => false
    };

    private static bool IsDocument(string mime)
    {
        var lower = mime.ToLowerInvariant();

        if (lower.StartsWith("text/", StringComparison.Ordinal))
            return true;

        return lower switch
        {
            "application/msword" => true,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => true,
            "application/vnd.ms-excel" => true,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => true,
            "application/vnd.ms-powerpoint" => true,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => true,
            "application/vnd.oasis.opendocument.text" => true,
            "application/vnd.oasis.opendocument.spreadsheet" => true,
            "application/vnd.oasis.opendocument.presentation" => true,
            "application/rtf" => true,
            "application/json" => true,
            "application/xml" => true,
            "application/x-yaml" => true,
            "application/yaml" => true,
            _ => false
        };
    }
}
