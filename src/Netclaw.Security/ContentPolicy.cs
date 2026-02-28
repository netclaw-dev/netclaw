using System.Collections.Frozen;

namespace Netclaw.Security;

/// <summary>
/// Configurable content security policy for file uploads.
/// </summary>
public sealed class ContentPolicy
{
    /// <summary>
    /// Default maximum file size: 20 MB.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 20 * 1024 * 1024;

    /// <summary>
    /// MIME types allowed through the content scanner.
    /// Defaults to image types supported by vision models.
    /// </summary>
    public FrozenSet<string> AllowedMimeTypes { get; init; } = DefaultAllowedMimeTypes;

    /// <summary>
    /// Maximum file size in bytes.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;

    public static readonly FrozenSet<string> DefaultAllowedMimeTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
